#import <Foundation/Foundation.h>
#import <Security/Security.h>
// Compile the production implementation, suppressing only automatic app startup.
#define load ufbTestSuppressStartup
#include "../../Runtime/Plugins/iOS/UIFrameBackup.mm"
#undef load

@interface TestUploadTask : NSObject
@property NSString *taskDescription;
@property NSUInteger taskIdentifier;
@property NSHTTPURLResponse *response;
@property BOOL canceled;
- (void)cancel;
@end
@implementation TestUploadTask
- (void)cancel { _canceled=YES; }
@end

static void Check(BOOL condition, NSString *message) {
    if (!condition) @throw [NSException exceptionWithName:@"TestFailure" reason:message userInfo:nil];
}

static UFBEngine *Engine(NSString *directory) {
    // Ownership/state tests do not create OS sessions or touch the user's Keychain.
    UFBEngine *engine=[UFBEngine alloc];
    engine.directory=directory; engine.wifi=NSURLSession.sharedSession;
    engine.jobs=[NSMutableDictionary new]; engine.tasks=[NSMutableDictionary new];
    engine.bodiesWifi=[NSMutableDictionary new]; engine.bodiesAny=[NSMutableDictionary new];
    return engine;
}

static NSMutableDictionary *Job(NSString *identifier) {
    return [@{@"id":identifier, @"generation":@"execution-1", @"state":@1, @"released":@NO,
        @"key":@"backup-key", @"sha256":@"content-hash", @"size":@17} mutableCopy];
}

static TestUploadTask *Task(NSDictionary *job, NSUInteger taskId) {
    TestUploadTask *task=[TestUploadTask new]; task.taskDescription=UFBTaskName(job); task.taskIdentifier=taskId;
    task.response=[[NSHTTPURLResponse alloc] initWithURL:[NSURL URLWithString:@"https://example.test/backup"]
        statusCode:200 HTTPVersion:@"HTTP/1.1" headerFields:nil];
    return task;
}

static void Complete(UFBEngine *engine, TestUploadTask *task) {
    NSDictionary *response=@{@"completed":@YES, @"uploadId":@"backup-key", @"backupId":@"backup-key",
        @"sha256":@"content-hash", @"size":@17, @"offset":@17};
    NSData *body=[NSJSONSerialization dataWithJSONObject:response options:0 error:nil];
    [engine URLSession:engine.wifi dataTask:(id)task didReceiveData:body];
    [engine URLSession:engine.wifi task:(id)task didCompleteWithError:nil];
}

int main(int argc, const char **argv) {
    @autoreleasepool {
        @try {
            NSString *directory=[NSString stringWithUTF8String:argv[1]];
            NSString *identifier=@"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            UFBEngine *engine=Engine(directory); NSMutableDictionary *job=Job(identifier);
            [engine save:job]; job[@"state"]=@3;
            Check([engine.jobs[identifier][@"state"] intValue]==1,@"Mutating a candidate must not change committed state");
            job=[engine.jobs[identifier] mutableCopy]; TestUploadTask *task=Task(job,1);
            engine.tasks[identifier]=(id)task;
            Complete(engine,task);
            NSDictionary *status=[engine call:@{@"op":@"status", @"id":identifier}];
            Check([status[@"state"] intValue]==3 && [status[@"released"] boolValue],@"Normal completion must publish after saving");
            NSDictionary *disk=[NSJSONSerialization JSONObjectWithData:[NSData dataWithContentsOfFile:[engine path:identifier]] options:0 error:nil];
            Check([disk[@"state"] intValue]==3,@"Completion must be durable");

            job=Job(identifier); [engine save:job]; task=Task(job,2); engine.tasks[identifier]=(id)task;
            TestUploadTask *peer=Task(Job(@"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),3);
            engine.tasks[@"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"]=(id)peer;
            NSString *savedPath=[engine path:identifier];
            engine.directory=[directory stringByAppendingPathComponent:@"missing-parent/store"];
            Complete(engine,task);
            NSException *failure=engine.repositoryFailure;
            Check(failure!=nil,@"Completion persistence failure must stop the repository");
            Check([engine.jobs[identifier][@"state"] intValue]==1 && ![engine.jobs[identifier][@"released"] boolValue],@"Failed write must not publish Completed or released");
            Check(peer.canceled && !engine.tasks[identifier],@"Fault must cancel dependent work and release callback ownership");
            disk=[NSJSONSerialization JSONObjectWithData:[NSData dataWithContentsOfFile:savedPath] options:0 error:nil];
            Check([disk[@"state"] intValue]==1,@"Failed completion must leave the committed record unchanged");
            for (NSString *op in @[@"status",@"wake",@"pause"]) {
                NSException *observed=nil;
                @try { [engine call:@{@"op":op,@"id":identifier}]; } @catch(NSException *error) { observed=error; }
                Check(observed==failure,@"Bridge operations must expose the original repository failure");
            }
            engine.directory=directory; NSException *observed=nil;
            @try { [engine save:job]; } @catch(NSException *error) { observed=error; }
            Check(observed==failure,@"Restoring disk access must not silently restart a faulted owner");
            puts("iOS persistence: durable publication, completion failure, dependent cancellation and fault propagation passed");
            return 0;
        } @catch (NSException *error) { fprintf(stderr,"%s\n",error.description.UTF8String); return 1; }
    }
}
