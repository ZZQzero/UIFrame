#import <Foundation/Foundation.h>
#import <Security/Security.h>
#import <UIKit/UIKit.h>
#import "PluginBase/AppDelegateListener.h"

static void UFBRequire(BOOL ok, NSString *message) {
    if (!ok) @throw [NSException exceptionWithName:@"UIFrameBackup" reason:message userInfo:nil];
}
static void UFBError(NSError *error) { if (error) UFBRequire(NO,error.localizedDescription); }
static BOOL UFBTerminal(NSInteger state) { return state==3 || state==4 || state==6 || state==7 || state==8; }
static NSString *UFBTaskName(NSDictionary *job) {
    return [NSString stringWithFormat:@"%@:%@",job[@"id"],job[@"generation"]];
}
static NSString *UFBTaskID(NSURLSessionTask *task) { return [task.taskDescription componentsSeparatedByString:@":"].firstObject; }

@interface UFBEngine : NSObject <NSURLSessionDataDelegate, NSURLSessionTaskDelegate, AppDelegateListener>
@property NSMutableDictionary<NSString*, NSDictionary*> *jobs;
@property NSMutableDictionary<NSString*, NSURLSessionUploadTask*> *tasks;
@property NSMutableDictionary<NSNumber*, NSMutableData*> *bodiesWifi;
@property NSMutableDictionary<NSNumber*, NSMutableData*> *bodiesAny;
@property NSMutableDictionary<NSString*, id> *completions;
@property NSURLSession *wifi;
@property NSURLSession *any;
@property NSString *directory;
@property NSInteger recovered;
@property NSException *repositoryFailure;
+ (instancetype)shared;
- (NSDictionary*)call:(NSDictionary*)request;
@end

@implementation UFBEngine
+ (void)load {
    // Register before Unity starts: background relaunch may have no C# service yet.
    [self shared];
}
+ (instancetype)shared {
    static UFBEngine *instance; static dispatch_once_t once;
    dispatch_once(&once, ^{ instance=[UFBEngine new]; }); return instance;
}
- (instancetype)init {
    if ((self=[super init])) {
        _jobs=[NSMutableDictionary new]; _tasks=[NSMutableDictionary new];
        _bodiesWifi=[NSMutableDictionary new]; _bodiesAny=[NSMutableDictionary new]; _completions=[NSMutableDictionary new];
        _directory=[NSSearchPathForDirectoriesInDomains(NSApplicationSupportDirectory,NSUserDomainMask,YES).firstObject stringByAppendingPathComponent:@"UIFrameTransfers"];
        UnityRegisterAppDelegateListener(self);
        @try {
        NSError *error=nil;
        [[NSFileManager defaultManager] createDirectoryAtPath:_directory withIntermediateDirectories:YES attributes:@{NSFileProtectionKey:NSFileProtectionCompleteUntilFirstUserAuthentication} error:&error]; UFBError(error);
        NSURL *directoryURL=[NSURL fileURLWithPath:_directory];
        [directoryURL setResourceValue:@YES forKey:NSURLIsExcludedFromBackupKey error:&error]; UFBError(error);
        for (NSString *name in [[NSFileManager defaultManager] contentsOfDirectoryAtPath:_directory error:&error]) {
            if (![name.pathExtension isEqual:@"json"]) continue;
            NSData *data=[NSData dataWithContentsOfFile:[_directory stringByAppendingPathComponent:name] options:0 error:&error]; UFBError(error);
            NSDictionary *j=[NSJSONSerialization JSONObjectWithData:data options:0 error:&error]; UFBError(error);
            _jobs[j[@"id"]]=j;
        }
        UFBError(error);
        _wifi=[self session:YES]; _any=[self session:NO];
        [self recover:_wifi]; [self recover:_any];
        } @catch (NSException *exception) { [self failRepository:exception]; }
    }
    return self;
}
- (NSURLSession*)session:(BOOL)wifi {
    NSString *identifier=[NSString stringWithFormat:@"%@.uiframe.backup.v1.%@",NSBundle.mainBundle.bundleIdentifier,wifi?@"wifi":@"any"];
    NSURLSessionConfiguration *c=[NSURLSessionConfiguration backgroundSessionConfigurationWithIdentifier:identifier];
    c.sessionSendsLaunchEvents=YES; c.discretionary=NO; c.allowsCellularAccess=!wifi;
    c.allowsExpensiveNetworkAccess=!wifi; c.allowsConstrainedNetworkAccess=!wifi;
    c.waitsForConnectivity=YES; c.timeoutIntervalForResource=7*24*60*60;
    c.HTTPMaximumConnectionsPerHost=2;
    return [NSURLSession sessionWithConfiguration:c delegate:self delegateQueue:NSOperationQueue.mainQueue];
}
- (NSString*)path:(NSString*)identifier { return [_directory stringByAppendingPathComponent:[identifier stringByAppendingString:@".json"]]; }
- (void)failRepository:(NSException*)exception {
    if (!_repositoryFailure) {
        _repositoryFailure=exception;
        NSLog(@"UIFrame backup repository stopped: %@",exception.reason);
        for (NSURLSessionTask *task in _tasks.allValues) [task cancel];
    }
}
- (void)save:(NSDictionary*)j {
    if (_repositoryFailure) @throw _repositoryFailure;
    @try {
        NSError *error=nil;
        NSDictionary *snapshot=[j copy];
        NSData *data=[NSJSONSerialization dataWithJSONObject:snapshot options:0 error:&error]; UFBError(error);
        [data writeToFile:[self path:j[@"id"]] options:NSDataWritingAtomic|NSDataWritingFileProtectionCompleteUntilFirstUserAuthentication error:&error]; UFBError(error);
        _jobs[j[@"id"]]=snapshot;
    } @catch (NSException *exception) { [self failRepository:exception]; @throw; }
}
- (NSMutableDictionary*)keyQuery:(NSString*)identifier {
    return [@{(__bridge id)kSecClass:(__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService:@"UIFrame.BackgroundBackup.v1",(__bridge id)kSecAttrAccount:identifier} mutableCopy];
}
- (void)removeToken:(NSString*)identifier {
    OSStatus result=SecItemDelete((__bridge CFDictionaryRef)[self keyQuery:identifier]);
    UFBRequire(result==errSecSuccess || result==errSecItemNotFound,[NSString stringWithFormat:@"Keychain delete failed (%d)",(int)result]);
}
- (void)storeToken:(NSString*)token identifier:(NSString*)identifier {
    [self removeToken:identifier]; NSMutableDictionary *q=[self keyQuery:identifier];
    q[(__bridge id)kSecValueData]=[token dataUsingEncoding:NSUTF8StringEncoding];
    q[(__bridge id)kSecAttrAccessible]=(__bridge id)kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly;
    OSStatus result=SecItemAdd((__bridge CFDictionaryRef)q,NULL);
    UFBRequire(result==errSecSuccess,[NSString stringWithFormat:@"Keychain save failed (%d)",(int)result]);
}
- (NSString*)token:(NSString*)identifier {
    NSMutableDictionary *q=[self keyQuery:identifier]; q[(__bridge id)kSecReturnData]=@YES;
    CFTypeRef data=NULL; OSStatus result=SecItemCopyMatching((__bridge CFDictionaryRef)q,&data);
    UFBRequire(result==errSecSuccess,[NSString stringWithFormat:@"Background credential unavailable (%d)",(int)result]);
    return [[NSString alloc] initWithData:CFBridgingRelease(data) encoding:NSUTF8StringEncoding];
}
- (void)recover:(NSURLSession*)session {
    [session getAllTasksWithCompletionHandler:^(NSArray<__kindof NSURLSessionTask*> *tasks) {
        dispatch_async(dispatch_get_main_queue(), ^{
            @try {
                if (self.repositoryFailure) { for (NSURLSessionTask *task in tasks) [task cancel]; return; }
                for (NSURLSessionUploadTask *task in tasks) {
                    if (!task.taskDescription || task.state==NSURLSessionTaskStateCompleted) { [task cancel]; continue; }
                    NSString *identifier=UFBTaskID(task);
                    NSMutableDictionary *j=[self.jobs[identifier] mutableCopy];
                    if (j && ![task.taskDescription isEqual:UFBTaskName(j)]) { [task cancel]; continue; }
                    if (!j || UFBTerminal([j[@"state"] integerValue])) { [task cancel]; if (!j) continue; }
                    self.tasks[identifier]=task;
                    j[@"released"]=@NO; [self save:j];
                    if (!UFBTerminal([j[@"state"] integerValue]) && task.state==NSURLSessionTaskStateSuspended) [task resume];
                }
                self.recovered++;
                if (self.recovered==2) {
                    for (NSDictionary *stored in self.jobs.allValues) {
                        NSMutableDictionary *j=[stored mutableCopy];
                        if (!self.tasks[j[@"id"]]) { j[@"released"]=@YES; [self save:j]; }
                        [self schedule:j];
                    }
                }
            } @catch (NSException *exception) { [self failRepository:exception];
                for (NSURLSessionTask *task in tasks) [task cancel]; }
        });
    }];
}
- (void)schedule:(NSMutableDictionary*)j {
    if (_repositoryFailure) @throw _repositoryFailure;
    if (_recovered!=2 || _tasks[j[@"id"]] || UFBTerminal([j[@"state"] integerValue])) return;
    @try {
        NSString *payload=[NSHomeDirectory() stringByAppendingPathComponent:j[@"payload"]];
        NSError *error=nil;
        NSDictionary *attributes=[[NSFileManager defaultManager] attributesOfItemAtPath:payload error:&error]; UFBError(error);
        UFBRequire([attributes[NSFileSize] longLongValue]==[j[@"size"] longLongValue],@"Staged payload size mismatch");
        NSMutableURLRequest *request=[NSMutableURLRequest requestWithURL:[NSURL URLWithString:j[@"url"]]];
        request.HTTPMethod=@"PUT";
        [request setValue:[@"Bearer " stringByAppendingString:[self token:j[@"id"]]] forHTTPHeaderField:@"Authorization"];
        [request setValue:j[@"account"] forHTTPHeaderField:@"X-Backup-Account-SHA256"];
        [request setValue:@"application/octet-stream" forHTTPHeaderField:@"Content-Type"];
        NSURLSession *session=[j[@"wifiOnly"] boolValue]?_wifi:_any;
        // Intent is durable before the OS task exists; recovery matches taskDescription.
        j[@"state"]=@1; j[@"released"]=@NO; j[@"generation"]=NSUUID.UUID.UUIDString; [self save:j];
        NSURLSessionUploadTask *task=[session uploadTaskWithRequest:request fromFile:[NSURL fileURLWithPath:payload]];
        task.taskDescription=UFBTaskName(j); _tasks[j[@"id"]]=task; [task resume];
    } @catch (NSException *exception) {
        if (_repositoryFailure) @throw;
        NSURLSessionTask *task=_tasks[j[@"id"]]; [task cancel];
        j[@"state"]=@6; j[@"released"]=@(task==nil); j[@"error"]=exception.reason; [self save:j];
    }
}
- (NSDictionary*)call:(NSDictionary*)request {
    NSString *identifier=request[@"id"], *op=request[@"op"];
    UFBRequire([identifier isKindOfClass:NSString.class] && identifier.length==32 &&
        [identifier rangeOfCharacterFromSet:[[NSCharacterSet characterSetWithCharactersInString:@"0123456789abcdef"] invertedSet]].location==NSNotFound,@"Invalid backup task ID");
    if (_repositoryFailure) {
        if ([op isEqual:@"pause"] || [op isEqual:@"cancel"]) [_tasks[identifier] cancel];
        @throw _repositoryFailure;
    }
    NSMutableDictionary *j=[_jobs[identifier] mutableCopy];
    if ([op isEqual:@"submit"]) {
        if (!j) {
            NSString *prefix=[NSHomeDirectory() stringByAppendingString:@"/"];
            NSString *payload=[request[@"payload"] stringByStandardizingPath];
            UFBRequire([payload hasPrefix:prefix],@"Native background payload must be inside the app sandbox");
            NSError *error=nil;
            [[NSFileManager defaultManager] setAttributes:@{NSFileProtectionKey:NSFileProtectionCompleteUntilFirstUserAuthentication} ofItemAtPath:payload error:&error]; UFBError(error);
            [self storeToken:request[@"token"] identifier:identifier];
            j=[request mutableCopy]; [j removeObjectForKey:@"token"]; [j removeObjectForKey:@"op"];
            j[@"payload"]=[payload substringFromIndex:prefix.length]; j[@"state"]=@0; j[@"released"]=@YES;
            [self save:j];
        }
        [self schedule:j];
    } else if ([op isEqual:@"wake"]) {
        UFBRequire(j!=nil,@"Native transfer not found"); [self schedule:j];
    } else if ([op isEqual:@"pause"] || [op isEqual:@"cancel"]) {
        NSInteger state=[j[@"state"] integerValue];
        UFBRequire(!j || state!=3 || ![op isEqual:@"cancel"],@"A committed backup cannot be canceled");
        if (j && state!=3 && state!=8 && ([op isEqual:@"cancel"] || state==0 || state==1)) {
            j[@"state"]=[op isEqual:@"cancel"]?@8:@4; [self save:j];
            [_tasks[identifier] cancel];
        }
    } else if ([op isEqual:@"forget"]) {
        UFBRequire(!j || ([j[@"released"] boolValue] && UFBTerminal([j[@"state"] integerValue])),@"Transfer still owns its payload");
        if (j) {
            [self removeToken:identifier]; NSError *error=nil;
            [[NSFileManager defaultManager] removeItemAtPath:[self path:identifier] error:&error]; UFBError(error);
            [_jobs removeObjectForKey:identifier]; j=nil;
        }
    } else UFBRequire([op isEqual:@"status"],@"Unknown backup command");
    j=[_jobs[identifier] mutableCopy];
    if (!j) return @{ @"exists":@NO, @"released":@YES };
    return @{ @"exists":@YES, @"released":j[@"released"], @"state":j[@"state"],
        @"error":j[@"error"]?:@"", @"backupId":j[@"backupId"]?:@"", @"confirmedBytes":j[@"confirmedBytes"]?:@0 };
}
- (NSMutableDictionary*)bodies:(NSURLSession*)session { return session==_wifi?_bodiesWifi:_bodiesAny; }
- (void)URLSession:(NSURLSession*)session dataTask:(NSURLSessionDataTask*)task didReceiveData:(NSData*)data {
    NSMutableDictionary *bodies=[self bodies:session]; NSNumber *key=@(task.taskIdentifier);
    NSMutableData *body=bodies[key]; if (!body) { body=[NSMutableData new]; bodies[key]=body; }
    if (body.length+data.length>65536) { [task cancel]; return; }
    [body appendData:data];
}
- (void)URLSession:(NSURLSession*)session task:(NSURLSessionTask*)task willPerformHTTPRedirection:(NSHTTPURLResponse*)response newRequest:(NSURLRequest*)request completionHandler:(void (^)(NSURLRequest*))completionHandler {
    completionHandler(nil); // Never forward a bearer token to a redirect destination.
}
- (void)URLSession:(NSURLSession*)session task:(NSURLSessionTask*)task didCompleteWithError:(NSError*)error {
    if (!task.taskDescription) return;
    NSString *identifier=UFBTaskID(task);
    NSMutableDictionary *j=[_jobs[identifier] mutableCopy];
    NSMutableDictionary *bodies=[self bodies:session]; NSData *body=bodies[@(task.taskIdentifier)];
    [bodies removeObjectForKey:@(task.taskIdentifier)];
    // An old completion cannot release a replacement task or remove its credential.
    if (!j || ![task.taskDescription isEqual:UFBTaskName(j)]) return;
    NSURLSessionTask *current=_tasks[identifier];
    if (current && current.taskIdentifier!=task.taskIdentifier) return;
    [_tasks removeObjectForKey:identifier];
    if (_repositoryFailure) return;
    @try {
        j[@"released"]=@YES;
        if (!UFBTerminal([j[@"state"] integerValue])) {
            NSInteger code=[(NSHTTPURLResponse*)task.response statusCode];
            NSError *parseError=nil;
            NSDictionary *response=body?[NSJSONSerialization JSONObjectWithData:body options:0 error:&parseError]:nil;
            BOOL verified=NO;
            @try { verified=!error && code==200 && [response isKindOfClass:NSDictionary.class] && [response[@"completed"] boolValue]
                && [response[@"uploadId"] isEqual:j[@"key"]] && [response[@"backupId"] isEqual:j[@"key"]]
                && [response[@"sha256"] isEqual:j[@"sha256"]] && [response[@"size"] isEqual:j[@"size"]] && [response[@"offset"] isEqual:j[@"size"]]; }
            @catch (NSException *exception) { parseError=[NSError errorWithDomain:@"UIFrameBackup" code:1 userInfo:@{NSLocalizedDescriptionKey:exception.reason}]; }
            if (verified) { j[@"state"]=@3; j[@"backupId"]=response[@"backupId"]; j[@"confirmedBytes"]=j[@"size"]; }
            else {
                j[@"state"]=(code==401 || code==403 || code==413 || code==507)?@6:@7;
                j[@"error"]=error.localizedDescription?:parseError.localizedDescription?:[NSString stringWithFormat:@"Server did not confirm verified backup (HTTP %ld)",(long)code];
            }
        }
        [self save:j];
    } @catch (NSException *exception) { [self failRepository:exception]; }
}
- (void)onHandleEventsForBackgroundURLSession:(NSNotification*)notification {
    for (NSString *identifier in notification.userInfo) {
        if (_repositoryFailure && [identifier hasPrefix:[NSBundle.mainBundle.bundleIdentifier stringByAppendingString:@".uiframe.backup.v1."]]) {
            void (^completion)(void)=notification.userInfo[identifier]; dispatch_async(dispatch_get_main_queue(),completion); continue;
        }
        if ([identifier isEqual:_wifi.configuration.identifier] || [identifier isEqual:_any.configuration.identifier])
            _completions[identifier]=[notification.userInfo[identifier] copy];
    }
}
- (void)URLSessionDidFinishEventsForBackgroundURLSession:(NSURLSession*)session {
    void (^completion)(void)=_completions[session.configuration.identifier];
    [_completions removeObjectForKey:session.configuration.identifier];
    if (completion) dispatch_async(dispatch_get_main_queue(),completion);
}
@end

extern "C" char *UFBCall(const char *json) {
    __block NSDictionary *result;
    void (^call)(void)=^{
        @try {
            NSError *error=nil;
            NSDictionary *request=[NSJSONSerialization JSONObjectWithData:[[NSString stringWithUTF8String:json] dataUsingEncoding:NSUTF8StringEncoding] options:0 error:&error]; UFBError(error);
            result=[[UFBEngine shared] call:request];
        } @catch (NSException *exception) { result=@{@"exists":@NO,@"error":exception.reason?:@"Native backup failure"}; }
    };
    if (NSThread.isMainThread) call(); else dispatch_sync(dispatch_get_main_queue(),call);
    NSData *data=[NSJSONSerialization dataWithJSONObject:result options:0 error:nil];
    return strdup([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String);
}
extern "C" void UFBFree(void *pointer) { free(pointer); }
