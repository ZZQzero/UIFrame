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
    if(code) @throw [NSException exceptionWithName:@"UIFrameBackupRepository" reason:[NSString stringWithFormat:@"Backup repository error %d, SQLite %d, phase %u, commit %d: %s",code,status.sqlite_code,status.phase,status.committed,status.message] userInfo:nil];
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
static NSString *UFBTag(NSString *store,NSString *task,NSNumber *generation){return [NSString stringWithFormat:@"%@:%@:%@",store,task,generation];}
static NSArray<NSString*> *UFBParts(NSString *tag) {
    NSArray *parts=[tag componentsSeparatedByString:@":"];
    UFBRequire(parts.count==3 && UFBIdentity(parts[0],64) && UFBIdentity(parts[1],32) && [parts[2] longLongValue]>0,@"Invalid background task identity");return parts;
}
static void *UFBQueueKey=&UFBQueueKey;

@interface UFBEngine : NSObject <NSURLSessionDataDelegate,NSURLSessionTaskDelegate,AppDelegateListener>
@property dispatch_queue_t queue;
@property NSURLSession *wifi;
@property NSURLSession *any;
@property NSMutableDictionary<NSString*,NSNumber*> *stores;
@property NSMutableDictionary<NSString*,NSURLSessionTask*> *tasks;
@property NSMutableDictionary<NSString*,NSMutableData*> *bodies;
@property NSMutableDictionary<NSString*,NSString*> *bodyErrors;
@property NSMutableDictionary<NSString*,NSException*> *failures;
@property NSMutableDictionary<NSString*,id> *completions;
@property NSMutableSet<NSString*> *orphans;
@property NSMutableOrderedSet<NSString*> *waitingStores;
@property NSUInteger buffered;
@property NSInteger recovered;
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
        _stores=[NSMutableDictionary new];_tasks=[NSMutableDictionary new];_bodies=[NSMutableDictionary new];_bodyErrors=[NSMutableDictionary new];
        _failures=[NSMutableDictionary new];_completions=[NSMutableDictionary new];_orphans=[NSMutableSet new];_waitingStores=[NSMutableOrderedSet new];
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
    configuration.timeoutIntervalForResource=7*24*60*60;configuration.HTTPMaximumConnectionsPerHost=2;
    NSOperationQueue *delegate=[NSOperationQueue new];delegate.maxConcurrentOperationCount=1;delegate.underlyingQueue=_queue;
    return [NSURLSession sessionWithConfiguration:configuration delegate:self delegateQueue:delegate];
}
- (uint64_t)store:(NSString*)identity {
    UFBRequire(UFBIdentity(identity,64),@"Invalid repository identity");if(_failures[identity])@throw _failures[identity];
    NSNumber *existing=_stores[identity];if(existing)return existing.unsignedLongLongValue;
    auto status=UFBStatus();uint64_t handle=0;UFBCheck(ufbackup_open(UFBRoot().UTF8String,identity.UTF8String,"","",0,&handle,&status),status);
    _stores[identity]=@(handle);return handle;
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
    return [[NSString alloc] initWithData:CFBridgingRelease(data) encoding:NSUTF8StringEncoding];
}
- (void)storeToken:(NSString*)token tag:(NSString*)tag {
    UFBRequire([token isKindOfClass:NSString.class] && token.length>0,@"Missing background credential");
    NSString *existing=[self token:tag required:NO];if(existing){UFBRequire([existing isEqual:token],@"Credential changed for the same execution generation");return;}
    NSMutableDictionary *query=[self keyQuery:tag];query[(__bridge id)kSecValueData]=[token dataUsingEncoding:NSUTF8StringEncoding];
    query[(__bridge id)kSecAttrAccessible]=(__bridge id)kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly;
    OSStatus status=SecItemAdd((__bridge CFDictionaryRef)query,NULL);UFBRequire(status==errSecSuccess,[NSString stringWithFormat:@"Credential persistence failed (%d)",(int)status]);
}
- (void)removeToken:(NSString*)tag {
    OSStatus status=SecItemDelete((__bridge CFDictionaryRef)[self keyQuery:tag]);
    UFBRequire(status==errSecSuccess || status==errSecItemNotFound,[NSString stringWithFormat:@"Credential release failed (%d)",(int)status]);
}
- (NSString*)key:(NSURLSession*)session task:(NSURLSessionTask*)task {return [NSString stringWithFormat:@"%@:%lu",session.configuration.identifier,(unsigned long)task.taskIdentifier];}
- (void)release:(NSString*)identity record:(NSDictionary*)record {
    NSString *tag=UFBTag(identity,record[@"id"],record[@"attempt_generation"]);
    [self removeToken:tag];UFBCommand([self store:identity],UFB_RELEASE,@[record[@"id"],record[@"attempt_generation"],@YES,@YES]);
}
- (void)finish:(NSString*)identity record:(NSDictionary*)record outcome:(NSNumber*)outcome backup:(NSString*)backup error:(NSString*)error {
    UFBCommand([self store:identity],UFB_FINISH,@[record[@"id"],record[@"attempt_generation"],outcome,backup,error,@(UFBNow()),record[@"byte_count"],record[@"sha256"]]);
}
- (void)recover:(NSURLSession*)session {
    [session getAllTasksWithCompletionHandler:^(NSArray<__kindof NSURLSessionTask*> *tasks){dispatch_async(self.queue,^{
        for(NSURLSessionTask *task in tasks) {
            @autoreleasepool {
                NSString *identity=nil;
                @try {
                    if(!task.taskDescription.length){[self.orphans addObject:[self key:session task:task]];[task cancel];continue;}
                    NSArray *parts=UFBParts(task.taskDescription);identity=parts[0];
                    self.tasks[task.taskDescription]=task;
                    NSArray *rows=UFBCommand([self store:identity],UFB_ATTEMPT,@[parts[1],@([parts[2] longLongValue])]);
                    UFBRequire(rows.count==1,@"Background task has no matching repository attempt");NSDictionary *row=rows[0];
                    if([row[@"current_generation"] longLongValue]!=[parts[2] longLongValue] || [row[@"execution_state"] integerValue]>=2){[task cancel];continue;}
                    NSDictionary *info=UFBCommand([self store:identity],UFB_INFO,@[]).firstObject;
                    if([info[@"paused"] boolValue] || [row[@"desired_action"] integerValue]!=0){[task cancel];continue;}
                    UFBCommand([self store:identity],UFB_SUBMITTED,@[parts[1],@([parts[2] longLongValue]),[NSString stringWithFormat:@"%lu",(unsigned long)task.taskIdentifier],task.taskDescription]);
                    if([row[@"execution_state"] integerValue]==0 && UFBCommand([self store:identity],UFB_START,@[parts[1],@([parts[2] longLongValue])]).count==0){[task cancel];continue;}
                    if(task.state==NSURLSessionTaskStateSuspended)[task resume];
                } @catch(NSException *error){[task cancel];if(identity)[self fail:identity error:error];else NSLog(@"Invalid UIFrame background task: %@",error.reason);}
            }
        }
        self.recovered++;if(self.recovered==2 && self.orphans.count==0)[self discoverStores];
    });}];
}
- (void)discoverStores {
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
- (void)schedule:(NSString*)identity {
    if(_recovered!=2 || _orphans.count!=0)return;
    uint64_t handle=[self store:identity];NSDictionary *info=UFBCommand(handle,UFB_INFO,@[]).firstObject;long long cursor=0;
    for(;;) {
        NSArray *page=UFBCommand(handle,UFB_ATTEMPTS,@[@(cursor),@2,@100]);
        for(NSDictionary *row in page) {
            cursor=[row[@"sequence"] longLongValue];NSString *tag=UFBTag(identity,row[@"id"],row[@"current_generation"]);
            if(_tasks[tag])continue;
            if([row[@"execution_state"] integerValue]>=2){[self release:identity record:row];continue;}
            if([row[@"execution_state"] integerValue]==1) {
                [self finish:identity record:row outcome:@3 backup:@"" error:@"Previous system executor ended; reconcile the server result"];
                [self release:identity record:row];continue;
            }
            if([info[@"paused"] boolValue] || [row[@"desired_action"] integerValue]!=0) {
                [self finish:identity record:row outcome:[row[@"execution_state"] integerValue]==0?@4:@3 backup:@"" error:@"Stopped before system execution"];[self release:identity record:row];continue;
            }
            if(_tasks.count>=16){[_waitingStores addObject:identity];return;}
            @try {
            NSString *payload=[[[UFBRoot() stringByAppendingPathComponent:identity] stringByAppendingPathComponent:row[@"relative_path"]] stringByStandardizingPath];
            NSError *error=nil;NSDictionary *attributes=[[NSFileManager defaultManager] attributesOfItemAtPath:payload error:&error];UFBError(error);
            UFBRequire([attributes[NSFileSize] longLongValue]==[row[@"byte_count"] longLongValue],@"Staged payload size changed");
            [[NSFileManager defaultManager] setAttributes:@{NSFileProtectionKey:NSFileProtectionCompleteUntilFirstUserAuthentication} ofItemAtPath:payload error:&error];UFBError(error);
            NSMutableURLRequest *request=[NSMutableURLRequest requestWithURL:[NSURL URLWithString:[NSString stringWithFormat:@"%@/v1/uploads/%@/background",info[@"server"],row[@"idempotency_key"]]]];
            request.HTTPMethod=@"PUT";[request setValue:[@"Bearer " stringByAppendingString:[self token:tag required:YES]] forHTTPHeaderField:@"Authorization"];
            [request setValue:UFBHash(info[@"account"]) forHTTPHeaderField:@"X-Backup-Account-SHA256"];[request setValue:@"application/octet-stream" forHTTPHeaderField:@"Content-Type"];
            NSURLSession *session=[row[@"wifi_only"] boolValue]?_wifi:_any;
            NSURLSessionUploadTask *task=[session uploadTaskWithRequest:request fromFile:[NSURL fileURLWithPath:payload]];
            task.taskDescription=tag;_tasks[tag]=task;
            @try {
                UFBCommand(handle,UFB_SUBMITTED,@[row[@"id"],row[@"current_generation"],[NSString stringWithFormat:@"%lu",(unsigned long)task.taskIdentifier],tag]);
                if(UFBCommand(handle,UFB_START,@[row[@"id"],row[@"current_generation"]]).count==0)[task cancel];else [task resume];
            } @catch(NSException *failure){[task cancel];@throw;}
            } @catch(NSException *failure) {
                if([failure.name isEqual:@"UIFrameBackupRepository"] || _tasks[tag])@throw;
                [self finish:identity record:row outcome:[row[@"execution_state"] integerValue]==1?@3:@2 backup:@"" error:failure.reason];
                [self release:identity record:row];
            }
        }
        if(page.count<100)break;
    }
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
    if([op isEqual:@"recover"]) {UFBRequire(![self hasTasks:identity],@"Await native task release before recovering repository");[self detach:identity];[_failures removeObjectForKey:identity];}
    uint64_t handle=[self store:identity];
    if([op isEqual:@"submit"]) {
        NSArray *rows=UFBCommand(handle,UFB_TASK,@[request[@"id"]]);UFBRequire(rows.count==1,@"Task not found");NSDictionary *row=rows[0];
        UFBRequire([row[@"current_generation"] isEqual:request[@"generation"]],@"Obsolete native submission generation");
        NSString *tag=UFBTag(identity,row[@"id"],row[@"current_generation"]);[self storeToken:request[@"token"] tag:tag];[self schedule:identity];
    } else if([op isEqual:@"wake"] || [op isEqual:@"recover"])[self schedule:identity];
    else if([op isEqual:@"sync"]) {
        NSString *prefix=[identity stringByAppendingString:@":"];
        for(NSString *tag in _tasks) {
            if(![tag hasPrefix:prefix])continue;NSArray *parts=UFBParts(tag);
            NSDictionary *row=UFBCommand(handle,UFB_ATTEMPT,@[parts[1],@([parts[2] longLongValue])]).firstObject;
            if([row[@"desired_action"] integerValue]!=0 || [UFBCommand(handle,UFB_INFO,@[]).firstObject[@"paused"] boolValue])[_tasks[tag] cancel];
        }
        [self schedule:identity];
    }
    else if([op isEqual:@"pause"] || [op isEqual:@"stop"]) {
        NSString *prefix=[identity stringByAppendingString:@":"];
        for(NSString *tag in _tasks) {
            if(![tag hasPrefix:prefix])continue;NSArray *parts=UFBParts(tag);
            if([op isEqual:@"pause"] || [parts[1] isEqual:request[@"id"]])[_tasks[tag] cancel];
        }
        [self schedule:identity];
    } else UFBRequire(NO,@"Unknown native backup adapter command");
    [self trim:identity];return @{@"exists":@YES};
}
- (void)URLSession:(NSURLSession*)session dataTask:(NSURLSessionDataTask*)task didReceiveData:(NSData*)data {
    NSString *key=[self key:session task:task];NSMutableData *body=_bodies[key];if(!body){body=[NSMutableData new];_bodies[key]=body;}
    if(data.length>65536-body.length || data.length>1024*1024-_buffered){_bodyErrors[key]=@"Backup response buffer capacity exceeded";[task cancel];return;}
    [body appendData:data];_buffered+=data.length;
}
- (void)URLSession:(NSURLSession*)session task:(NSURLSessionTask*)task willPerformHTTPRedirection:(NSHTTPURLResponse*)response newRequest:(NSURLRequest*)request completionHandler:(void (^)(NSURLRequest*))completionHandler {completionHandler(nil);}
- (void)URLSession:(NSURLSession*)session task:(NSURLSessionTask*)task didCompleteWithError:(NSError*)error {
    NSString *key=[self key:session task:task],*identity=nil;NSData *body=_bodies[key];NSString *bodyError=_bodyErrors[key];
    _buffered-=body.length;[_bodies removeObjectForKey:key];[_bodyErrors removeObjectForKey:key];
    if([_orphans containsObject:key]){[_orphans removeObject:key];if(_recovered==2 && _orphans.count==0)[self discoverStores];return;}
    @try {
        NSArray *parts=UFBParts(task.taskDescription);identity=parts[0];NSString *tag=task.taskDescription;
        NSURLSessionTask *owned=_tasks[tag];if(owned && owned!=task && owned.taskIdentifier!=task.taskIdentifier)return;
        [_tasks removeObjectForKey:tag];
        NSArray *rows=UFBCommand([self store:identity],UFB_ATTEMPT,@[parts[1],@([parts[2] longLongValue])]);UFBRequire(rows.count==1,@"Completed attempt is missing");
        NSDictionary *row=rows[0];
        if([row[@"execution_state"] integerValue]<2) {
            NSInteger code=[(NSHTTPURLResponse*)task.response statusCode];NSError *parseError=nil;
            NSDictionary *response=body?[NSJSONSerialization JSONObjectWithData:body options:0 error:&parseError]:nil;
            BOOL verified=!error && !bodyError && code==200 && [response isKindOfClass:NSDictionary.class] && [response[@"completed"] isKindOfClass:NSNumber.class] && [response[@"completed"] boolValue]
                && [response[@"uploadId"] isEqual:row[@"idempotency_key"]] && [response[@"sha256"] isEqual:row[@"sha256"]]
                && [response[@"size"] isEqual:row[@"byte_count"]] && [response[@"offset"] isEqual:row[@"byte_count"]]
                && [response[@"backupId"] isKindOfClass:NSString.class] && [response[@"backupId"] length]>0;
            NSInteger outcome=verified?1:[row[@"execution_state"] integerValue]==0?4:3;
            NSString *message=verified?@"":bodyError?:error.localizedDescription?:parseError.localizedDescription?:[NSString stringWithFormat:@"Backup confirmation failed (HTTP %ld)",(long)code];
            [self finish:identity record:row outcome:@(outcome) backup:verified?response[@"backupId"]:@"" error:message];
        }
        [self release:identity record:row];
        uint64_t handle=[self store:identity];NSArray *clean=UFBCommand(handle,UFB_CLEANUP_PAGE,@[@"",@32]);
        for(NSDictionary *item in clean)UFBCommand(handle,UFB_CLEANUP_RUN,@[item[@"id"],item[@"updated_utc"],@(UFBNow())]);
        [self drainWaiting];[self schedule:identity];[self trim:identity];
    } @catch(NSException *failure){if(identity)[self fail:identity error:failure];else NSLog(@"Unidentified backup callback: %@",failure.reason);}
}
- (void)onHandleEventsForBackgroundURLSession:(NSNotification*)notification {
    dispatch_async(_queue,^{
        for(NSString *identifier in notification.userInfo) {
            if([identifier isEqual:self.wifi.configuration.identifier] || [identifier isEqual:self.any.configuration.identifier])self.completions[identifier]=[notification.userInfo[identifier] copy];
        }
    });
}
- (void)URLSessionDidFinishEventsForBackgroundURLSession:(NSURLSession*)session {
    void (^completion)(void)=_completions[session.configuration.identifier];[_completions removeObjectForKey:session.configuration.identifier];
    // SQL failures stop the affected store, but never retain the OS completion.
    if(completion)dispatch_async(dispatch_get_main_queue(),completion);
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
                void (^call)(void)=^{@try {result=[engine call:request];} @catch(NSException *failure){result=@{@"exists":@NO,@"error":failure.reason?:@"Native backup failure"};}};
                if(dispatch_get_specific(UFBQueueKey))call();else dispatch_sync(engine.queue,call);
            }
        } @catch(NSException *failure){result=@{@"exists":@NO,@"error":failure.reason?:@"Native backup failure"};}
        NSData *data=[NSJSONSerialization dataWithJSONObject:result options:0 error:nil];return strdup([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String);
    }
}
extern "C" void UFBFree(void *value){free(value);}
