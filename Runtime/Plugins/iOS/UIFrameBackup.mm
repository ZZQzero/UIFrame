#import <Foundation/Foundation.h>
#import <Security/Security.h>
#import <UIKit/UIKit.h>
#import <CommonCrypto/CommonDigest.h>
#import "PluginBase/AppDelegateListener.h"
#include "ufbackup.h"
#include "ufsqlite_client.hpp"

static void UFBRequire(BOOL valid,NSString *message) {
    if(!valid) @throw [NSException exceptionWithName:@"UIFrameBackup" reason:message userInfo:nil];
}
static void UFBError(NSError *error) {if(error)UFBRequire(NO,error.localizedDescription);}
static long long UFBNow(){return 621355968000000000LL+(long long)(NSDate.date.timeIntervalSince1970*10000000.0);}
static BOOL UFBControlExpired(NSDictionary *row) {
    long long deadline=[row[@"deadline_utc"] longLongValue];return deadline>0 && deadline<=UFBNow();
}
static NSString *UFBResponseFailure(NSURLRequest *original,NSURLResponse *response) {
    if(!original.URL || !response.URL || ![original.URL isEqual:response.URL])return @"Background response target differs from the submitted target";
    return nil;
}
static NSString *UFBRoot() {
    NSString *root=[NSSearchPathForDirectoriesInDomains(NSApplicationSupportDirectory,NSUserDomainMask,YES).firstObject stringByAppendingPathComponent:@"UIFrameBackup"];
    NSError *error=nil;
    [[NSFileManager defaultManager] createDirectoryAtPath:root withIntermediateDirectories:YES attributes:@{NSFileProtectionKey:NSFileProtectionCompleteUntilFirstUserAuthentication} error:&error];UFBError(error);
    [[NSURL fileURLWithPath:root] setResourceValue:@YES forKey:NSURLIsExcludedFromBackupKey error:&error];UFBError(error);return root;
}
static BOOL UFBIdentity(NSString *value,NSUInteger length) {
    return [value isKindOfClass:NSString.class] && value.length==length && [value rangeOfCharacterFromSet:[[NSCharacterSet characterSetWithCharactersInString:@"0123456789abcdef"] invertedSet]].location==NSNotFound;
}
static NSString *UFBHash(NSString *value) {
    NSData *data=[value dataUsingEncoding:NSUTF8StringEncoding];unsigned char hash[CC_SHA256_DIGEST_LENGTH];CC_SHA256(data.bytes,(CC_LONG)data.length,hash);
    NSMutableString *text=[NSMutableString stringWithCapacity:64];for(unsigned i=0;i<sizeof(hash);++i)[text appendFormat:@"%02x",hash[i]];return text;
}
static ufb_status UFBStatus(){ufb_status value{};value.size=sizeof(value);value.abi=UFB_ABI;return value;}
static void UFBCheck(int code,const ufb_status &status) {
    if(code) @throw [NSException exceptionWithName:@"UIFrameBackupRepository" reason:[NSString stringWithFormat:@"Backup repository error %d, SQLite %d, phase %u, commit %d: %s",code,status.sqlite_code,status.phase,status.committed,status.message] userInfo:@{@"code":@(code)}];
}
static NSArray<NSDictionary*> *UFBCommand(uint64_t handle,unsigned command,NSArray *arguments) {
    using namespace ufsqlite;
    try {
        Bytes input;number(input,arguments.count,4);
        for(id value in arguments) {
            if(value==NSNull.null) Value().write(input);
            else if([value isKindOfClass:NSNumber.class]) Value((int64_t)[value longLongValue]).write(input);
            else if([value isKindOfClass:NSString.class]) Value(std::string([value UTF8String])).write(input);
            else UFBRequire(NO,@"Unsupported repository command argument");
            UFBRequire(input.size()<=1024*1024,@"Backup command exceeds 1 MiB");
        }
        Bytes output(1024*1024);auto status=UFBStatus();
        UFBCheck(ufbackup_call(handle,command,input.data(),(uint32_t)input.size(),output.data(),(uint32_t)output.size(),&status),status);
        output.resize(status.length);auto tables=decode(output);if(tables.empty())return @[];
        auto &table=tables.back();NSMutableArray *result=[NSMutableArray arrayWithCapacity:table.rows.size()];
        NSMutableArray<NSString*> *names=[NSMutableArray arrayWithCapacity:table.columns.size()];
        for(auto &name:table.columns)[names addObject:[[NSString alloc] initWithBytes:name.data() length:name.size() encoding:NSUTF8StringEncoding]];
        for(auto &values:table.rows) {
            NSMutableDictionary *row=[NSMutableDictionary dictionaryWithCapacity:values.size()];
            for(size_t i=0;i<values.size();++i) {
                auto &value=values[i];id item=NSNull.null;
                if(value.type==1)item=@(value.integer);
                else if(value.type==3)item=[[NSString alloc] initWithBytes:value.text.data() length:value.text.size() encoding:NSUTF8StringEncoding];
                else if(value.type==4)item=[NSData dataWithBytes:value.text.data() length:value.text.size()];
                else UFBRequire(value.type==0,@"Unexpected repository field type");
                UFBRequire(item!=nil,@"Invalid UTF-8 repository field");row[names[i]]=item;
            }
            [result addObject:row];
        }
        return result;
    } catch(const std::exception &error) {UFBRequire(NO,[NSString stringWithUTF8String:error.what()]);return nil;}
}
static NSString *UFBTag(NSString *store,NSString *kind,NSString *item,NSNumber *generation){return [NSString stringWithFormat:@"%@:%@:%@:%@",store,kind,item,generation];}
static NSArray<NSString*> *UFBParts(NSString *tag) {
    NSArray *parts=[tag componentsSeparatedByString:@":"];
    UFBRequire(parts.count==4 && UFBIdentity(parts[0],64) && ([parts[1] isEqual:@"c"] || [parts[1] isEqual:@"u"])
        && [parts[2] length]>0 && [parts[2] length]<=64 && UFBIdentity(parts[2],[parts[2] length])
        && ([parts[1] isEqual:@"c"] || [parts[3] longLongValue]>0),@"Invalid background task identity");
    return parts;
}
static void UFBProtect(NSString *path) {
    NSError *error=nil;
    [[NSFileManager defaultManager] setAttributes:@{NSFileProtectionKey:NSFileProtectionCompleteUntilFirstUserAuthentication} ofItemAtPath:path error:&error];UFBError(error);
}
static void *UFBQueueKey=&UFBQueueKey;

@interface UFBEngine : NSObject <NSURLSessionDataDelegate,NSURLSessionTaskDelegate,AppDelegateListener>
@property dispatch_queue_t queue;
@property NSURLSession *wifi;
@property NSURLSession *any;
@property NSMutableDictionary<NSString*,NSNumber*> *stores;
@property NSMutableDictionary<NSString*,NSURLSessionTask*> *tasks;
@property NSMutableDictionary<NSString*,NSMutableData*> *bodies;
@property NSMutableDictionary<NSString*,NSString*> *responseErrors;
@property NSMutableDictionary<NSString*,NSException*> *failures;
@property NSMutableDictionary<NSString*,id> *completions;
@property NSMutableSet<NSString*> *finishedEvents;
@property NSMutableSet<NSString*> *orphans;
@property NSMutableOrderedSet<NSString*> *waitingStores;
@property NSUInteger buffered;
@property NSInteger recovered;
@property dispatch_group_t recoveryGate;
@property BOOL recoveryFinished;
@property NSException *recoveryError;
@property NSMutableOrderedSet<NSString*> *flushStores;
@property BOOL flushing;
+ (instancetype)shared;
- (NSDictionary*)call:(NSDictionary*)request;
@end
@implementation UFBEngine
+ (void)load {[self shared];}
+ (instancetype)shared {
    static UFBEngine *value;static dispatch_once_t once;dispatch_once(&once,^{value=[UFBEngine new];});return value;
}
- (instancetype)init {
    if((self=[super init])) {
        _queue=dispatch_queue_create("com.uiframe.backup.adapter",DISPATCH_QUEUE_SERIAL);dispatch_queue_set_specific(_queue,UFBQueueKey,UFBQueueKey,NULL);
        _stores=[NSMutableDictionary new];_tasks=[NSMutableDictionary new];_bodies=[NSMutableDictionary new];_responseErrors=[NSMutableDictionary new];
        _failures=[NSMutableDictionary new];_completions=[NSMutableDictionary new];_finishedEvents=[NSMutableSet new];
        _orphans=[NSMutableSet new];_waitingStores=[NSMutableOrderedSet new];
        _flushStores=[NSMutableOrderedSet new];_recoveryGate=dispatch_group_create();dispatch_group_enter(_recoveryGate);
        UnityRegisterAppDelegateListener(self);
        _wifi=[self session:YES];_any=[self session:NO];[self recover:_wifi];[self recover:_any];
    }
    return self;
}
- (NSURLSession*)session:(BOOL)wifi {
    NSString *identifier=[NSString stringWithFormat:@"%@.uiframe.backup.catalog.%@",NSBundle.mainBundle.bundleIdentifier,wifi?@"wifi":@"any"];
    NSURLSessionConfiguration *configuration=[NSURLSessionConfiguration backgroundSessionConfigurationWithIdentifier:identifier];
    configuration.sessionSendsLaunchEvents=YES;configuration.discretionary=NO;configuration.waitsForConnectivity=YES;
    configuration.allowsCellularAccess=!wifi;configuration.allowsExpensiveNetworkAccess=!wifi;configuration.allowsConstrainedNetworkAccess=!wifi;
    configuration.timeoutIntervalForResource=24*60*60;configuration.HTTPMaximumConnectionsPerHost=3;
    NSOperationQueue *delegate=[NSOperationQueue new];delegate.maxConcurrentOperationCount=1;delegate.underlyingQueue=_queue;
    return [NSURLSession sessionWithConfiguration:configuration delegate:self delegateQueue:delegate];
}
- (uint64_t)store:(NSString*)identity {
    UFBRequire(UFBIdentity(identity,64),@"Invalid repository identity");if(_failures[identity])@throw _failures[identity];
    NSNumber *existing=_stores[identity];if(existing)return existing.unsignedLongLongValue;
    auto status=UFBStatus();uint64_t handle=0;UFBCheck(ufbackup_open(UFBRoot().UTF8String,identity.UTF8String,"","",0,&handle,&status),status);
    _stores[identity]=@(handle);
    NSString *folder=[UFBRoot() stringByAppendingPathComponent:identity];UFBProtect(folder);
    for(NSString *name in @[@"catalog.sqlite",@"catalog.sqlite-wal",@"catalog.sqlite-shm"]) {
        NSString *file=[folder stringByAppendingPathComponent:name];if([[NSFileManager defaultManager] fileExistsAtPath:file])UFBProtect(file);
    }
    return handle;
}
- (void)detach:(NSString*)identity {
    NSNumber *value=_stores[identity];[_stores removeObjectForKey:identity];if(!value)return;
    auto status=UFBStatus();UFBCheck(ufbackup_close(value.unsignedLongLongValue,&status),status);
}
- (BOOL)hasTasks:(NSString*)identity {
    NSString *prefix=[identity stringByAppendingString:@":"];for(NSString *tag in _tasks)if([tag hasPrefix:prefix])return YES;return NO;
}
- (void)trim:(NSString*)identity {
    if([self hasTasks:identity])return;
    if(_failures[identity]){[self detach:identity];return;}
    NSNumber *handle=_stores[identity];if(handle && UFBCommand(handle.unsignedLongLongValue,UFB_ATTEMPTS,@[@0,@2,@1]).count==0)[self detach:identity];
}
- (void)fail:(NSString*)identity error:(NSException*)error {
    if(!_failures[identity])_failures[identity]=error;
    [_waitingStores removeObject:identity];
    NSLog(@"UIFrame backup repository %@ stopped: %@",identity,error.reason);
    NSString *prefix=[identity stringByAppendingString:@":"];
    for(NSString *tag in _tasks)if([tag hasPrefix:prefix])[_tasks[tag] cancel];
    @try{[self trim:identity];}@catch(NSException *cleanup){NSLog(@"UIFrame backup shutdown failed: %@",cleanup.reason);}
}
- (NSMutableDictionary*)keyQuery:(NSString*)tag {
    return [@{(__bridge id)kSecClass:(__bridge id)kSecClassGenericPassword,(__bridge id)kSecAttrService:@"UIFrame.BackgroundBackup.Catalog",(__bridge id)kSecAttrAccount:tag} mutableCopy];
}
- (NSString*)token:(NSString*)tag required:(BOOL)required {
    NSMutableDictionary *query=[self keyQuery:tag];query[(__bridge id)kSecReturnData]=@YES;CFTypeRef data=NULL;
    OSStatus status=SecItemCopyMatching((__bridge CFDictionaryRef)query,&data);
    if(status==errSecItemNotFound && !required)return nil;
    UFBRequire(status==errSecSuccess,[NSString stringWithFormat:@"Background credential unavailable (%d)",(int)status]);
    NSString *text=[[NSString alloc] initWithData:CFBridgingRelease(data) encoding:NSUTF8StringEncoding];
    UFBRequire(text!=nil,@"Invalid credential encoding");return text;
}
- (void)storeToken:(NSString*)token tag:(NSString*)tag {
    UFBRequire([token isKindOfClass:NSString.class] && token.length>0,@"Missing background credential");
    NSString *existing=[self token:tag required:NO];if(existing){UFBRequire([existing isEqual:token],@"Credential changed while still owned");return;}
    NSMutableDictionary *query=[self keyQuery:tag];query[(__bridge id)kSecValueData]=[token dataUsingEncoding:NSUTF8StringEncoding];
    query[(__bridge id)kSecAttrAccessible]=(__bridge id)kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly;
    OSStatus status=SecItemAdd((__bridge CFDictionaryRef)query,NULL);UFBRequire(status==errSecSuccess,[NSString stringWithFormat:@"Credential persistence failed (%d)",(int)status]);
}
- (void)removeToken:(NSString*)tag {
    OSStatus status=SecItemDelete((__bridge CFDictionaryRef)[self keyQuery:tag]);
    UFBRequire(status==errSecSuccess || status==errSecItemNotFound,[NSString stringWithFormat:@"Credential release failed (%d)",(int)status]);
}
- (NSString*)api:(NSString*)identity row:(NSDictionary*)row {return UFBTag(identity,@"a",row[@"id"],row[@"current_generation"]);}
- (NSString*)descriptor:(NSString*)identity item:(NSString*)item generation:(NSNumber*)generation {return UFBTag(identity,@"d",item,generation);}
- (NSString*)key:(NSURLSession*)session task:(NSURLSessionTask*)task {return [NSString stringWithFormat:@"%@:%lu",session.configuration.identifier,(unsigned long)task.taskIdentifier];}
- (void)recover:(NSURLSession*)session {
    [session getAllTasksWithCompletionHandler:^(NSArray<__kindof NSURLSessionTask*> *tasks){dispatch_async(self.queue,^{
        for(NSURLSessionTask *task in tasks) {
            @autoreleasepool {
                NSString *identity=nil;
                @try {
                    if(!task.taskDescription.length){[self.orphans addObject:[self key:session task:task]];[task cancel];continue;}
                    NSArray *parts=UFBParts(task.taskDescription);identity=parts[0];self.tasks[task.taskDescription]=task;
                    uint64_t handle=[self store:identity];BOOL control=[parts[1] isEqual:@"c"];
                    NSDictionary *row=UFBCommand(handle,control?UFB_CONTROL:UFB_ATTEMPT,control?@[parts[2]]:@[parts[2],@([parts[3] longLongValue])]).firstObject;
                    UFBRequire(row!=nil,@"System task has no persisted intent");
                    if(control) {
                        if([row[@"released"] boolValue] || [row[@"state"] integerValue]>=4 || UFBControlExpired(row)){[task cancel];continue;}
                        if([row[@"state"] integerValue]==1 && ![UFBCommand(handle,UFB_CONTROL_SUBMITTED,@[parts[2],[self key:session task:task]]).firstObject[@"admitted"] boolValue]) {
                            UFBCommand(handle,UFB_CONTROL_FAIL,@[parts[2],@"Admission closed before recovery",@(UFBNow()),@YES]);[task cancel];
                        }
                    } else {
                        if([row[@"current_generation"] longLongValue]!=[parts[3] longLongValue] || [row[@"protocol_phase"] integerValue]!=1){[task cancel];continue;}
                        if([row[@"execution_state"] integerValue]==0 && ![UFBCommand(handle,UFB_UPLOAD_START,@[parts[2],@([parts[3] longLongValue]),[self key:session task:task],@(UFBNow())]).firstObject[@"started"] boolValue]){[task cancel];continue;}
                    }
                    if(task.state==NSURLSessionTaskStateSuspended)[task resume];
                } @catch(NSException *error){[task cancel];if(identity)[self fail:identity error:error];else NSLog(@"Invalid UIFrame background task: %@",error.reason);}
            }
        }
        self.recovered++;if(self.recovered==2 && self.orphans.count==0)[self discoverStores];
    });}];
}
- (void)recoverCredentials {
    // Only this service's live attributes; no secret values or photo history.
    NSDictionary *query=@{(__bridge id)kSecClass:(__bridge id)kSecClassGenericPassword,(__bridge id)kSecAttrService:@"UIFrame.BackgroundBackup.Catalog",(__bridge id)kSecReturnAttributes:@YES,(__bridge id)kSecMatchLimit:(__bridge id)kSecMatchLimitAll};
    CFTypeRef result=NULL;OSStatus status=SecItemCopyMatching((__bridge CFDictionaryRef)query,&result);
    UFBRequire(status==errSecSuccess || status==errSecItemNotFound,@"Credential inventory unavailable until protected storage is accessible");
    NSArray *items=CFBridgingRelease(result);
    for(NSDictionary *item in items) {
        NSString *tag=item[(__bridge id)kSecAttrAccount];NSArray *parts=[tag componentsSeparatedByString:@":"];
        UFBRequire(parts.count==4 && UFBIdentity(parts[0],64) && ([(NSString*)parts[1] isEqual:@"a"] || [(NSString*)parts[1] isEqual:@"d"]),@"Invalid owned credential identity");
        @try {
            uint64_t handle=[self store:parts[0]];
            NSDictionary *row=UFBCommand(handle,UFB_ATTEMPT,@[parts[2],@([parts[3] longLongValue])]).firstObject;
            NSString *column=[parts[1] isEqual:@"a"]?@"credential_reference":@"upload_reference";
            if(!row || ![row[column] isEqual:tag])[self removeToken:tag];
        } @catch(NSException *error){[self fail:parts[0] error:error];}
    }

}
- (void)discoverStores {
    if(!_recoveryFinished) {
        @try{[self recoverCredentials];}@catch(NSException *error){_recoveryError=error;NSLog(@"Backup credential recovery failed: %@",error.reason);}
        _recoveryFinished=YES;dispatch_group_leave(_recoveryGate);
    }
    @try {
        NSDirectoryEnumerator *directories=[[NSFileManager defaultManager] enumeratorAtURL:[NSURL fileURLWithPath:UFBRoot()] includingPropertiesForKeys:@[NSURLIsDirectoryKey] options:NSDirectoryEnumerationSkipsSubdirectoryDescendants errorHandler:^BOOL(NSURL *url,NSError *error){NSLog(@"Backup repository discovery failed: %@",error);return NO;}];
        for(NSURL *directory in directories) {
            @autoreleasepool {
                NSString *identity=directory.lastPathComponent;if(!UFBIdentity(identity,64))continue;
                @try{[self schedule:identity];[self trim:identity];}@catch(NSException *error){[self fail:identity error:error];}
            }
        }
    } @catch(NSException *error){NSLog(@"Backup root discovery failed: %@",error.reason);}
}
- (void)settle:(NSString*)identity {
    uint64_t handle=[self store:identity];BOOL paused=[UFBCommand(handle,UFB_INFO,@[]).firstObject[@"paused"] boolValue];
    for(NSDictionary *control in UFBCommand(handle,UFB_CONTROLS,@[@2,@""])) {
        NSString *tag=UFBTag(identity,@"c",control[@"id"],@0);NSURLSessionTask *task=_tasks[tag];
        NSInteger state=[control[@"state"] integerValue];
        BOOL pauseControl=paused && [control[@"kind"] integerValue]!=2;
        BOOL expired=UFBControlExpired(control);
        if(task){if(pauseControl || expired)[task cancel];continue;}
        if(state>=2 || pauseControl || expired) {
            if(state<4)UFBCommand(handle,UFB_CONTROL_RECOVER,@[control[@"id"],expired?@"Confirmation deadline reached":@"System control execution ended; reconcile its result",@(UFBNow()),@(pauseControl && !expired)]);
            UFBCommand(handle,UFB_CONTROL_RELEASE,@[control[@"id"]]);
        }
    }
    UFBCommand(handle,UFB_PROTOCOL_ACTIONS,@[@2,@(UFBNow())]);
    long long cursor=0;
    for(;;) {
        NSArray *rows=UFBCommand(handle,UFB_ATTEMPTS,@[@(cursor),@2,@100]);
        for(NSDictionary *__strong row in rows) {
            cursor=[row[@"sequence"] longLongValue];NSString *item=row[@"id"];NSNumber *generation=row[@"current_generation"];
            NSString *tag=UFBTag(identity,@"u",item,generation);NSURLSessionTask *task=_tasks[tag];
            if(task){if(paused || [row[@"desired_action"] integerValue]!=0)[task cancel];continue;}
            if(row[@"control_id"]!=NSNull.null)continue;
            if([row[@"execution_state"] integerValue]==1) {
                UFBCommand(handle,UFB_UPLOAD_END,@[item,generation,@0,@"Previous system upload ended; reconcile its result",@(UFBNow())]);
                row=UFBCommand(handle,UFB_TASK,@[item]).firstObject;
            }
            BOOL stopped=[row[@"protocol_phase"] integerValue]==3 || [row[@"state"] integerValue]==4;
            BOOL expired=[row[@"protocol_phase"] integerValue]==1 && [row[@"upload_expires_utc"] longLongValue]<=UFBNow();
            if(stopped || expired || [row[@"protocol_phase"] integerValue]==2) {
                // Deterministic keys also cover interruption between Keychain write
                // and response application; their owner is the persisted attempt.
                [self removeToken:[self descriptor:identity item:item generation:generation]];
                UFBCommand(handle,UFB_UPLOAD_RELEASE,@[item,generation,@(UFBNow())]);
            }
            if(stopped && ([row[@"protocol_phase"] integerValue]==3 || [row[@"desired_action"] integerValue]!=2)) {
                [self removeToken:[self api:identity row:row]];UFBCommand(handle,UFB_PROTOCOL_RELEASE,@[item,generation]);
            }
        }
        if(rows.count<100)break;
    }
    UFBCommand(handle,UFB_PROTOCOL_CLEANUP,@[@(UFBNow()),@32]);
    for(NSDictionary *item in UFBCommand(handle,UFB_CLEANUP_PAGE,@[@"",@32])) {
        @try{UFBCommand(handle,UFB_CLEANUP_RUN,@[item[@"id"],item[@"updated_utc"],@(UFBNow())]);}
        @catch(NSException *failure){
            if(![failure.name isEqual:@"UIFrameBackupRepository"] || [failure.userInfo[@"code"] integerValue]!=UFB_CLEANUP_FILE_FAILED)@throw;
            NSLog(@"UIFrame file cleanup failed (%@): %@",item[@"id"],failure.reason);
        }
    }
}
- (void)submit:(NSString*)identity row:(NSDictionary*)row control:(BOOL)control {
    @try {[self submitOwned:identity row:row control:control];}
    @catch(NSException *error) {
        // A persistence failure leaves ownership uncertain and stops the store.
        // Local file/credential failures before task creation affect this item.
        if([error.name isEqual:@"UIFrameBackupRepository"])@throw;
        NSString *item=row[control?@"id":@"task_id"];NSNumber *generation=control?@0:row[@"generation"];
        if(_tasks[UFBTag(identity,control?@"c":@"u",item,generation)])@throw;
        uint64_t handle=[self store:identity];
        if(control) {
            UFBCommand(handle,UFB_CONTROL_FAIL,@[item,error.reason,@(UFBNow()),@NO]);
            UFBCommand(handle,UFB_CONTROL_RELEASE,@[item]);
        } else UFBCommand(handle,UFB_UPLOAD_REJECT,@[item,generation,error.reason,@(UFBNow())]);
        [self settle:identity];
    }
}
- (void)submitOwned:(NSString*)identity row:(NSDictionary*)row control:(BOOL)control {
    uint64_t handle=[self store:identity];NSString *item=row[control?@"id":@"task_id"];NSNumber *generation=control?@0:row[@"generation"];
    NSString *tag=UFBTag(identity,control?@"c":@"u",item,generation);if(_tasks[tag])return;
    NSDictionary *info=UFBCommand(handle,UFB_INFO,@[]).firstObject;NSMutableURLRequest *request;
    if(control) {
        UFBRequire(!UFBControlExpired(row),@"Confirmation deadline reached");
        row=UFBCommand(handle,UFB_CONTROL_SEAL,@[item]).firstObject;
        NSArray *paths=@[@"/v2/backup/plans",@"/v2/backup/status",@"/v2/backup/cancellations"];
        request=[NSMutableURLRequest requestWithURL:[NSURL URLWithString:[info[@"server"] stringByAppendingString:paths[[row[@"kind"] unsignedIntegerValue]]]]];
        request.HTTPMethod=@"POST";[request setValue:@"application/json" forHTTPHeaderField:@"Content-Type"];
        [request setValue:[@"Bearer " stringByAppendingString:[self token:row[@"credential_reference"] required:YES]] forHTTPHeaderField:@"Authorization"];
    } else {
        NSError *error=nil;NSData *data=[[self token:row[@"upload_reference"] required:YES] dataUsingEncoding:NSUTF8StringEncoding];
        NSDictionary *descriptor=[NSJSONSerialization JSONObjectWithData:data options:0 error:&error];UFBError(error);
        request=[NSMutableURLRequest requestWithURL:[NSURL URLWithString:descriptor[@"url"]]];request.HTTPMethod=@"PUT";
        for(NSDictionary *header in descriptor[@"headers"])[request setValue:header[@"value"] forHTTPHeaderField:header[@"name"]];
    }
    NSString *payload=[[UFBRoot() stringByAppendingPathComponent:identity] stringByAppendingPathComponent:row[@"relative_path"]];
    NSError *error=nil;NSDictionary *attributes=[[NSFileManager defaultManager] attributesOfItemAtPath:payload error:&error];UFBError(error);
    UFBRequire([attributes[NSFileSize] longLongValue]==[row[@"byte_count"] longLongValue],@"Owned backup file size changed");UFBProtect(payload);
    NSURLSession *session=[row[@"wifi_only"] boolValue]?_wifi:_any;
    NSURLSessionUploadTask *task=[session uploadTaskWithRequest:request fromFile:[NSURL fileURLWithPath:payload]];task.taskDescription=tag;_tasks[tag]=task;
    if(control)task.earliestBeginDate=[NSDate dateWithTimeIntervalSince1970:([row[@"not_before_utc"] longLongValue]-621355968000000000LL)/10000000.0];
    @try {
        if(control) {
            if(![UFBCommand(handle,UFB_CONTROL_SUBMITTED,@[item,[self key:session task:task]]).firstObject[@"admitted"] boolValue]) {
                UFBCommand(handle,UFB_CONTROL_FAIL,@[item,@"Admission closed before transfer",@(UFBNow()),@YES]);[task cancel];return;
            }
        }
        else if(![UFBCommand(handle,UFB_UPLOAD_START,@[item,generation,[self key:session task:task],@(UFBNow())]).firstObject[@"started"] boolValue]){[task cancel];return;}
        [task resume];
    } @catch(NSException *failure){[task cancel];@throw;}
}
- (void)schedule:(NSString*)identity {[self schedule:identity flush:NO];}
- (void)aggregate:(NSString*)identity at:(long long)due {
    [_flushStores addObject:identity];if(_flushing)return;_flushing=YES;
    // A single bounded OS assertion covers the <=1 second aggregation tail.
    // Its expiration path also submits a future URLSession request before release.
    dispatch_async(dispatch_get_main_queue(),^{
        __block UIBackgroundTaskIdentifier assertion=UIBackgroundTaskInvalid;
        __block BOOL finished=NO;
        dispatch_block_t finish=^{
            if(finished)return;finished=YES;
            NSArray *stores=self.flushStores.array;[self.flushStores removeAllObjects];self.flushing=NO;
            @try {for(NSString *store in stores)@try{[self schedule:store flush:YES];[self trim:store];}@catch(NSException *error){[self fail:store error:error];}}
            @finally {dispatch_async(dispatch_get_main_queue(),^{if(assertion!=UIBackgroundTaskInvalid)[UIApplication.sharedApplication endBackgroundTask:assertion];});}
        };
        assertion=[UIApplication.sharedApplication beginBackgroundTaskWithName:@"UIFrame backup batch" expirationHandler:^{dispatch_async(self.queue,finish);}];
        int64_t delay=assertion==UIBackgroundTaskInvalid?0:MAX(0,MIN(10000000LL,due-UFBNow()))*100;
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW,delay),self.queue,finish);
    });
}
- (void)schedule:(NSString*)identity flush:(BOOL)flush {
    if(_recovered!=2 || _orphans.count!=0){[_waitingStores addObject:identity];return;}
    uint64_t handle=[self store:identity];[self settle:identity];
    if(_tasks.count>=16){[_waitingStores addObject:identity];UFBCommand(handle,UFB_SYSTEM_SCHEDULED,@[@2]);return;}
    for(NSDictionary *row in UFBCommand(handle,UFB_CONTROLS,@[@2,@""])) {
        if(_tasks.count>=16)break;
        if([row[@"state"] integerValue]<=1)[self submit:identity row:row control:YES];
    }
    if(_tasks.count<16) {
        NSString *requestId=[[NSUUID.UUID.UUIDString lowercaseString] stringByReplacingOccurrencesOfString:@"-" withString:@""];
        long long now=UFBNow(),next=[UFBCommand(handle,UFB_PROTOCOL_WAKE,@[@2]).firstObject[@"next_utc"] longLongValue];
        BOOL future=flush || next>now+10000000LL;
        NSArray *created=UFBCommand(handle,UFB_CONTROL_CREATE,@[requestId,@2,@(now),@(future),@(flush)]);
        if(created.count)[self submit:identity row:created.firstObject control:YES];
        else if(next>now && !future)[self aggregate:identity at:next];
    }
    NSUInteger photos=0,globalPhotos=0;
    for(NSString *tag in _tasks){NSArray *parts=UFBParts(tag);if([parts[1] isEqual:@"u"]){globalPhotos++;if([parts[0] isEqual:identity])photos++;}}
    for(NSDictionary *row in UFBCommand(handle,UFB_UPLOADS,@[@2,@""])) {
        if(photos>=2 || globalPhotos>=14 || _tasks.count>=16){[_waitingStores addObject:identity];break;}
        if([row[@"execution_state"] integerValue]!=0 || [row[@"upload_expires_utc"] longLongValue]<=UFBNow())continue;
        [self submit:identity row:row control:NO];photos++;globalPhotos++;
    }
    // Existing URLSession tasks also own the next scheduling opportunity. Their
    // completion drains waiting stores, including photos admitted during a PUT
    // or control request. Admission does not require a new OS task per photo.
    if(_tasks.count && ([self hasTasks:identity] || [_waitingStores containsObject:identity]))UFBCommand(handle,UFB_SYSTEM_SCHEDULED,@[@2]);
}
- (void)drainWaiting {
    NSUInteger count=_waitingStores.count;
    while(count-- && _tasks.count<16 && _waitingStores.count) {
        NSString *identity=_waitingStores.firstObject;[_waitingStores removeObject:identity];
        @try{[self schedule:identity];[self trim:identity];}@catch(NSException *error){[self fail:identity error:error];}
    }
}
- (NSDictionary*)call:(NSDictionary*)request {
    NSString *op=request[@"op"];if([op isEqual:@"root"])return @{@"exists":@YES,@"root":UFBRoot()};
    NSString *identity=request[@"repository"];
    if([op isEqual:@"recover"]) {
        UFBRequire(![self hasTasks:identity],@"Await native release before recovering");
        [self detach:identity];[_failures removeObjectForKey:identity];
        [self recoverCredentials];_recoveryError=nil;
    } else if(_recoveryError)@throw _recoveryError;
    uint64_t handle=[self store:identity];
    if([op isEqual:@"accept"] || [op isEqual:@"resume"] || [op isEqual:@"reconcile"]) {
        NSDictionary *row=UFBCommand(handle,UFB_TASK,@[request[@"id"]]).firstObject;UFBRequire(row!=nil,@"Task not found");
        if([op isEqual:@"accept"])UFBRequire([row[@"current_generation"] isEqual:request[@"generation"]],@"Obsolete native admission generation");
        NSString *reference=[self api:identity row:row];[self storeToken:request[@"token"] tag:reference];
        @try {
            if([op isEqual:@"accept"])UFBCommand(handle,UFB_HANDOFF,@[row[@"id"],row[@"current_generation"],reference]);
            else if([op isEqual:@"resume"])UFBCommand(handle,UFB_PROTOCOL_RESUME,@[row[@"id"],reference,@(UFBNow())]);
            else UFBCommand(handle,UFB_PROTOCOL_RECONCILE,@[row[@"id"],@(UFBNow()),request[@"cancel"],reference]);
        } @catch(NSException *error) {
            @try{NSDictionary *current=UFBCommand(handle,UFB_TASK,@[row[@"id"]]).firstObject;if(![current[@"credential_reference"] isEqual:reference])[self removeToken:reference];}
            @catch(NSException *cleanup){NSLog(@"Credential cleanup failed: %@",cleanup.reason);}@throw;
        }
    } else if([@[@"wake",@"sync",@"recover",@"pause",@"stop"] containsObject:op])
        // Explicit admission flushes its tail before returning to SubmitAsync;
        // autonomous callbacks retain the bounded Plan/Query aggregation window.
        [self schedule:identity flush:[op isEqual:@"wake"]];
    else UFBRequire(NO,@"Unknown native backup adapter command");
    [self trim:identity];return @{@"exists":@YES};
}
- (void)URLSession:(NSURLSession*)session dataTask:(NSURLSessionDataTask*)task didReceiveData:(NSData*)data {
    // Storage success has no business response; never buffer an object PUT body.
    if(![task.taskDescription containsString:@":c:"])return;
    NSString *key=[self key:session task:task];NSMutableData *body=_bodies[key];if(!body){body=[NSMutableData new];_bodies[key]=body;}
    if(data.length>512*1024-body.length || data.length>2*1024*1024-_buffered){_responseErrors[key]=@"Control response buffer capacity exceeded";[task cancel];return;}
    [body appendData:data];_buffered+=data.length;
}
- (void)URLSession:(NSURLSession*)session task:(NSURLSessionTask*)task didFinishCollectingMetrics:(NSURLSessionTaskMetrics*)metrics {
    // Background sessions follow redirects without calling the redirect delegate.
    // Detect a reported redirect (including a round trip to the original URL),
    // but only the trusted server/gateway can prevent forwarding on this transport.
    if(metrics.redirectCount>0)_responseErrors[[self key:session task:task]]=@"Background endpoint violated the no-redirect contract";
}
- (void)URLSession:(NSURLSession*)session task:(NSURLSessionTask*)task didCompleteWithError:(NSError*)error {
    NSString *key=[self key:session task:task],*identity=nil;NSData *body=_bodies[key];
    NSString *responseError=_responseErrors[key]?:(!error?UFBResponseFailure(task.originalRequest,task.response):nil);
    _buffered-=body.length;[_bodies removeObjectForKey:key];[_responseErrors removeObjectForKey:key];
    if([_orphans containsObject:key]){[_orphans removeObject:key];if(_recovered==2 && _orphans.count==0)[self discoverStores];return;}
    @try {
        NSArray *parts=UFBParts(task.taskDescription);identity=parts[0];BOOL control=[parts[1] isEqual:@"c"];
        NSURLSessionTask *owned=_tasks[task.taskDescription];if(owned && owned!=task && owned.taskIdentifier!=task.taskIdentifier)return;
        [_tasks removeObjectForKey:task.taskDescription];uint64_t handle=[self store:identity];
        NSDictionary *row=UFBCommand(handle,control?UFB_CONTROL:UFB_ATTEMPT,control?@[parts[2]]:@[parts[2],@([parts[3] longLongValue])]).firstObject;
        UFBRequire(row!=nil,@"Completed system intent is missing");
        NSInteger status=[(NSHTTPURLResponse*)task.response statusCode];
        BOOL paused=[UFBCommand(handle,UFB_INFO,@[]).firstObject[@"paused"] boolValue];
        BOOL expired=control && UFBControlExpired(row);
        NSString *failure=responseError?:expired?@"Confirmation deadline reached":error?[NSString stringWithFormat:@"System transfer error %@/%ld",error.domain,(long)error.code]:[NSString stringWithFormat:@"HTTP %ld",(long)status];
        if(control) {
            if([row[@"state"] integerValue]<4) {
                if(!error && !responseError && status==200) {
                    NSString *response=[[NSString alloc] initWithData:body encoding:NSUTF8StringEncoding];
                    @try {
                        UFBRequire(response!=nil,@"Invalid control response UTF-8");
                        NSArray *descriptors=UFBCommand(handle,UFB_CONTROL_VALIDATE,@[parts[2],response]);
                        NSMutableArray *args=[NSMutableArray arrayWithArray:@[parts[2],response,@(UFBNow()),@(descriptors.count)]];
                        for(NSDictionary *descriptor in descriptors) {
                            NSString *reference=[self descriptor:identity item:descriptor[@"task_id"] generation:descriptor[@"generation"]];
                            [self storeToken:descriptor[@"descriptor"] tag:reference];[args addObject:descriptor[@"task_id"]];[args addObject:reference];
                        }
                        UFBCommand(handle,UFB_CONTROL_APPLY,args);
                    } @catch(NSException *invalid) {
                        if([invalid.name isEqual:@"UIFrameBackupRepository"] && [invalid.userInfo[@"code"] integerValue]!=1)@throw;
                        UFBCommand(handle,UFB_CONTROL_FAIL,@[parts[2],invalid.reason,@(UFBNow()),@NO]);
                    }
                } else UFBCommand(handle,UFB_CONTROL_FAIL,@[parts[2],failure,@(UFBNow()),@(paused && !expired && !responseError && [row[@"kind"] integerValue]!=2)]);
            }
            UFBCommand(handle,UFB_CONTROL_RELEASE,@[parts[2]]);
        } else {
            NSNumber *generation=@([parts[3] longLongValue]);
            if([row[@"protocol_phase"] integerValue]==1 && [row[@"execution_state"] integerValue]==1) {
                NSData *data=[[self token:row[@"upload_reference"] required:YES] dataUsingEncoding:NSUTF8StringEncoding];
                NSError *parse=nil;NSDictionary *descriptor=[NSJSONSerialization JSONObjectWithData:data options:0 error:&parse];UFBError(parse);
                BOOL success=!error && !responseError && [descriptor[@"successStatusCodes"] containsObject:@(status)];
                BOOL userPause=!responseError && (paused || [row[@"desired_action"] integerValue]==1);
                UFBCommand(handle,UFB_UPLOAD_END,@[parts[2],generation,success?@1:userPause?@2:@0,success?@"":failure,@(UFBNow())]);
            }
            NSDictionary *current=UFBCommand(handle,UFB_ATTEMPT,@[parts[2],generation]).firstObject;
            if([current[@"protocol_phase"] integerValue]!=1) {
                [self removeToken:[self descriptor:identity item:parts[2] generation:generation]];
                UFBCommand(handle,UFB_UPLOAD_RELEASE,@[parts[2],generation,@(UFBNow())]);
            }
        }
        [self drainWaiting];[self schedule:identity];[self trim:identity];
    } @catch(NSException *failure){if(identity)[self fail:identity error:failure];else NSLog(@"Unidentified backup callback: %@",failure.reason);}
}
- (void)onHandleEventsForBackgroundURLSession:(NSNotification*)notification {
    dispatch_async(_queue,^{
        for(NSString *identifier in notification.userInfo) {
            if(![identifier isEqual:self.wifi.configuration.identifier] && ![identifier isEqual:self.any.configuration.identifier])continue;
            void (^completion)(void)=[notification.userInfo[identifier] copy];
            if([self.finishedEvents containsObject:identifier]){[self.finishedEvents removeObject:identifier];dispatch_async(dispatch_get_main_queue(),completion);}
            else self.completions[identifier]=completion;
        }
    });
}
- (void)URLSessionDidFinishEventsForBackgroundURLSession:(NSURLSession*)session {
    NSString *identifier=session.configuration.identifier;void (^completion)(void)=_completions[identifier];[_completions removeObjectForKey:identifier];
    // Completion is independent of database success, including callback ordering.
    if(completion)dispatch_async(dispatch_get_main_queue(),completion);else [_finishedEvents addObject:identifier];
}
@end
extern "C" char *UFBCall(const char *json) {
    @autoreleasepool {
        __block NSDictionary *result;
        @try {
            NSError *error=nil;NSDictionary *request=[NSJSONSerialization JSONObjectWithData:[[NSString stringWithUTF8String:json] dataUsingEncoding:NSUTF8StringEncoding] options:0 error:&error];UFBError(error);
            UFBRequire([request isKindOfClass:NSDictionary.class],@"Invalid backup request");
            if([request[@"op"] isEqual:@"root"])result=@{@"exists":@YES,@"root":UFBRoot()};
            else {
                UFBEngine *engine=[UFBEngine shared];
                UFBRequire(!NSThread.isMainThread && !dispatch_get_specific(UFBQueueKey),@"Backup native admission must run on a worker thread");
                UFBRequire(dispatch_group_wait(engine.recoveryGate,dispatch_time(DISPATCH_TIME_NOW,30*NSEC_PER_SEC))==0,@"System background task recovery did not finish within 30 seconds");
                void (^call)(void)=^{@try {result=[engine call:request];} @catch(NSException *failure){result=@{@"exists":@NO,@"error":failure.reason?:@"Native backup failure"};}};
                dispatch_sync(engine.queue,call);
            }
        } @catch(NSException *failure){result=@{@"exists":@NO,@"error":failure.reason?:@"Native backup failure"};}
        NSData *data=[NSJSONSerialization dataWithJSONObject:result options:0 error:nil];return strdup([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String);
    }
}
extern "C" void UFBFree(void *value){free(value);}
