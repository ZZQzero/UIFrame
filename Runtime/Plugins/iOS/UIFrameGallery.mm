#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <Photos/Photos.h>
#import <PhotosUI/PhotosUI.h>
#import <ImageIO/ImageIO.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#import <Network/Network.h>
#include <atomic>

static std::atomic<bool> UFMWifi(false);
extern "C" bool UFMIsUnmeteredWifi() {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        nw_path_monitor_t monitor=nw_path_monitor_create();
        nw_path_monitor_set_update_handler(monitor, ^(nw_path_t path) {
            UFMWifi.store(nw_path_get_status(path)==nw_path_status_satisfied && nw_path_uses_interface_type(path,nw_interface_type_wifi)
                && !nw_path_is_expensive(path) && !nw_path_is_constrained(path));
        });
        nw_path_monitor_set_queue(monitor,dispatch_get_global_queue(QOS_CLASS_UTILITY,0)); nw_path_monitor_start(monitor);
        // Retain the monitor for the application lifetime.
        static nw_path_monitor_t retained; retained=monitor;
    });
    return UFMWifi.load();
}

@interface UFMJob : NSObject
@property(nonatomic,strong) NSDictionary *request;
@property(nonatomic,copy) NSString *identifier;
@property(nonatomic,copy) NSString *result;
@property(atomic) BOOL canceled;
@property(nonatomic) BOOL finished;
@property(nonatomic,strong) NSURL *securityRoot;
@end
@implementation UFMJob
@end

static NSMutableDictionary<NSString*,UFMJob*> *UFMJobs;
static NSOperationQueue *UFMQueue;
static NSString *UFMWindow;
static UIViewController *UFMPresented;
static id UFMPickerDelegate;

static void UFMInitialize() {
    static dispatch_once_t once;
    dispatch_once(&once, ^{ UFMJobs=[NSMutableDictionary new]; UFMQueue=[NSOperationQueue new]; UFMQueue.maxConcurrentOperationCount=2; });
}
static NSDictionary *UFMError(NSString *code, NSString *message) { return @{ @"status":@"error", @"code":code, @"error":message ?: @"Native operation failed." }; }
static NSError *UFMFailure(NSString *message) { return [NSError errorWithDomain:@"UIFrameGallery" code:1 userInfo:@{NSLocalizedDescriptionKey:message}]; }
static void UFMClean(UFMJob *job) {
    NSString *path=job.request[@"output"];
    if(path.length && [[NSFileManager defaultManager] fileExistsAtPath:path]) {
        NSError *error=nil; if(![[NSFileManager defaultManager] removeItemAtPath:path error:&error]) NSLog(@"UIFrameGallery cleanup: %@",error);
    }
}
static void UFMComplete(UFMJob *job, NSDictionary *result) {
    @synchronized(UFMJobs) {
        if(job.finished) return;
        job.finished=YES;
        if(job.securityRoot) { [job.securityRoot stopAccessingSecurityScopedResource]; job.securityRoot=nil; }
        if(job.canceled || ![result[@"status"] isEqual:@"ok"]) UFMClean(job);
        if(job.canceled) [UFMJobs removeObjectForKey:job.identifier];
        else { NSData *data=[NSJSONSerialization dataWithJSONObject:result options:0 error:nil]; job.result=[[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding]; }
    }
}
static NSString *UFMAccess() {
    PHAuthorizationStatus status=[PHPhotoLibrary authorizationStatusForAccessLevel:PHAccessLevelReadWrite];
    switch(status) {
        case PHAuthorizationStatusAuthorized:return @"Authorized";
        case PHAuthorizationStatusLimited:return @"Limited";
        case PHAuthorizationStatusDenied:return @"Denied";
        case PHAuthorizationStatusRestricted:return @"Restricted";
        default:return @"NotDetermined";
    }
}
static NSString *UFMMime(NSString *extension) {
    UTType *type=[UTType typeWithFilenameExtension:extension]; return type.preferredMIMEType ?: @"application/octet-stream";
}
static NSDictionary *UFMFile(NSString *path, NSString *name) {
    NSDictionary *attrs=[[NSFileManager defaultManager] attributesOfItemAtPath:path error:nil];
    return @{ @"path":path, @"name":name ?: path.lastPathComponent, @"mime":UFMMime(path.pathExtension), @"size":attrs[NSFileSize] ?: @0 };
}
static PHFetchOptions *UFMImageOptions() {
    PHFetchOptions *options=[PHFetchOptions new]; options.predicate=[NSPredicate predicateWithFormat:@"mediaType == %d",PHAssetMediaTypeImage];
    options.sortDescriptors=@[[NSSortDescriptor sortDescriptorWithKey:@"creationDate" ascending:NO]]; return options;
}
static NSURL *UFMResolveDirectory(UFMJob *job, NSString *bookmark, NSError **error) {
    NSData *data=[[NSData alloc] initWithBase64EncodedString:bookmark options:0];
    if(!data) { *error=UFMFailure(@"Invalid directory bookmark."); return nil; }
    BOOL stale=NO;
    NSURL *url=[NSURL URLByResolvingBookmarkData:data options:NSURLBookmarkResolutionWithoutUI relativeToURL:nil bookmarkDataIsStale:&stale error:error];
    if(!url || stale) { if(!*error) *error=UFMFailure(@"Directory bookmark is stale; select the directory again."); return nil; }
    if(![url startAccessingSecurityScopedResource]) { *error=UFMFailure(@"Directory authorization unavailable."); return nil; }
    job.securityRoot=url; return url;
}
static NSDictionary *UFMDirectory(UFMJob *job) {
    NSError *error=nil; NSURL *root=UFMResolveDirectory(job,job.request[@"path"],&error);
    if(!root) return UFMError(@"PermissionDenied",error.description);
    NSArray *keys=@[NSURLIsDirectoryKey,NSURLIsSymbolicLinkKey,NSURLFileSizeKey,NSURLContentModificationDateKey];
    __block NSError *enumerationError=nil;
    NSDirectoryEnumerator *enumerator=[[NSFileManager defaultManager] enumeratorAtURL:root includingPropertiesForKeys:keys options:0 errorHandler:^BOOL(NSURL *url,NSError *problem) { enumerationError=problem; return NO; }];
    NSMutableArray *items=[NSMutableArray new];
    for(NSURL *url in enumerator) {
        if(job.canceled) break;
        NSDictionary *values=[url resourceValuesForKeys:keys error:&error]; if(!values) return UFMError(@"ReadFailed",error.description);
        if([values[NSURLIsSymbolicLinkKey] boolValue]) { [enumerator skipDescendants]; continue; }
        if([values[NSURLIsDirectoryKey] boolValue]) { if(![job.request[@"recursive"] boolValue]) [enumerator skipDescendants]; continue; }
        NSString *mime=UFMMime(url.pathExtension); if(![mime hasPrefix:@"image/"]) continue;
        NSString *relative=[url.path substringFromIndex:root.path.length+1];
        NSDictionary *identity=@{@"bookmark":job.request[@"path"],@"relative":relative};
        NSString *identifier=[[NSString alloc] initWithData:[NSJSONSerialization dataWithJSONObject:identity options:0 error:nil] encoding:NSUTF8StringEncoding];
        [items addObject:@{@"id":identifier,@"source":@"directory",@"name":url.lastPathComponent,@"mime":mime,@"size":values[NSURLFileSizeKey] ?: @(-1),
            @"version":[NSString stringWithFormat:@"%.6f:%@",[values[NSURLContentModificationDateKey] timeIntervalSince1970],values[NSURLFileSizeKey]]}];
    }
    return enumerationError ? UFMError(@"ReadFailed",enumerationError.description) : @{ @"status":@"ok", @"items":items };
}
static NSDictionary *UFMLibrary(UFMJob *job) {
    NSString *access=UFMAccess();
    if(![access isEqual:@"Authorized"] && ![access isEqual:@"Limited"]) return UFMError(@"PermissionDenied",@"Photo library read access has not been granted.");
    NSMutableArray *items=[NSMutableArray new];
    if([job.request[@"op"] isEqual:@"albums"]) {
        NSMutableSet *seen=[NSMutableSet new];
        for(NSNumber *type in @[@(PHAssetCollectionTypeSmartAlbum),@(PHAssetCollectionTypeAlbum)]) {
            PHFetchResult *collections=[PHAssetCollection fetchAssetCollectionsWithType:(PHAssetCollectionType)type.integerValue subtype:PHAssetCollectionSubtypeAny options:nil];
            for(PHAssetCollection *collection in collections) {
                if(job.canceled) break;
                if([seen containsObject:collection.localIdentifier]) continue; [seen addObject:collection.localIdentifier];
                NSUInteger count=[PHAsset fetchAssetsInAssetCollection:collection options:UFMImageOptions()].count;
                [items addObject:@{ @"id":collection.localIdentifier, @"name":collection.localizedTitle ?: @"相册", @"count":@(count) }];
            }
        }
    } else {
        NSString *album=job.request[@"album"]; PHFetchResult<PHAsset*> *assets;
        if(album.length) {
            PHAssetCollection *collection=[PHAssetCollection fetchAssetCollectionsWithLocalIdentifiers:@[album] options:nil].firstObject;
            if(!collection) return UFMError(@"SourceUnavailable",@"Album is no longer accessible.");
            assets=[PHAsset fetchAssetsInAssetCollection:collection options:UFMImageOptions()];
        } else assets=[PHAsset fetchAssetsWithOptions:UFMImageOptions()];
        for(PHAsset *asset in assets) {
            if(job.canceled) break;
            PHAssetResource *resource=[PHAssetResource assetResourcesForAsset:asset].firstObject;
            NSString *name=resource.originalFilename ?: @"image";
            [items addObject:@{ @"id":asset.localIdentifier,@"name":name,@"mime":UFMMime(name.pathExtension),@"size":@(-1),
                @"width":@(asset.pixelWidth),@"height":@(asset.pixelHeight),@"version":[NSString stringWithFormat:@"%.6f",asset.modificationDate.timeIntervalSince1970] }];
        }
    }
    return @{ @"status":@"ok", @"items":items };
}
// Executed on the worker queue. PhotoKit copies resources without retaining a full-resolution NSData buffer.
static NSString *UFMSource(UFMJob *job, NSError **error) {
    if([job.request[@"source"] isEqual:@"file"]) return job.request[@"path"];
    if([job.request[@"source"] isEqual:@"directory"]) {
        NSDictionary *identity=[NSJSONSerialization JSONObjectWithData:[job.request[@"path"] dataUsingEncoding:NSUTF8StringEncoding] options:0 error:error];
        if(!identity) return nil;
        NSURL *root=UFMResolveDirectory(job,identity[@"bookmark"],error); if(!root) return nil;
        NSString *path=[[root.path stringByAppendingPathComponent:identity[@"relative"]] stringByStandardizingPath];
        NSString *resolved=[path stringByResolvingSymlinksInPath];
        if(![resolved hasPrefix:[[root.path stringByResolvingSymlinksInPath] stringByAppendingString:@"/"]]) { *error=UFMFailure(@"Image lies outside the granted directory."); return nil; }
        return path;
    }
    PHAsset *asset=[PHAsset fetchAssetsWithLocalIdentifiers:@[job.request[@"path"]] options:nil].firstObject;
    if(!asset) { *error=UFMFailure(@"Source is unavailable or permission was revoked."); return nil; }
    PHAssetResource *resource=nil;
    for(PHAssetResource *candidate in [PHAssetResource assetResourcesForAsset:asset]) {
        if(candidate.type==PHAssetResourceTypeFullSizePhoto) { resource=candidate; break; }
        if(candidate.type==PHAssetResourceTypePhoto) resource=candidate;
    }
    if(!resource) { *error=UFMFailure(@"No static photo representation available."); return nil; }
    NSString *path=[job.request[@"output"] stringByAppendingPathComponent:[@"source." stringByAppendingString:resource.originalFilename.pathExtension]];
    PHAssetResourceRequestOptions *options=[PHAssetResourceRequestOptions new]; options.networkAccessAllowed=YES;
    dispatch_semaphore_t semaphore=dispatch_semaphore_create(0); __block NSError *failure=nil;
    [[PHAssetResourceManager defaultManager] writeDataForAssetResource:resource toFile:[NSURL fileURLWithPath:path] options:options completionHandler:^(NSError *problem) { failure=problem; dispatch_semaphore_signal(semaphore); }];
    dispatch_semaphore_wait(semaphore,DISPATCH_TIME_FOREVER); *error=failure;
    return failure ? nil : path;
}
static NSDictionary *UFMRead(UFMJob *job) {
    NSError *error=nil; NSString *source=UFMSource(job,&error);
    if(!source) return UFMError(@"SourceUnavailable",error.description);
    if(job.canceled) return @{ @"status":@"canceled" };
    if([job.request[@"op"] isEqual:@"export"]) {
        NSString *path=[job.request[@"output"] stringByAppendingPathComponent:[@"image." stringByAppendingString:source.pathExtension]];
        if(![[NSFileManager defaultManager] copyItemAtPath:source toPath:path error:&error]) return UFMError(@"WriteFailed",error.description);
        return @{ @"status":@"ok", @"items":@[UFMFile(path,source.lastPathComponent)] };
    }
    CGImageSourceRef imageSource=CGImageSourceCreateWithURL((__bridge CFURLRef)[NSURL fileURLWithPath:source],NULL);
    if(!imageSource) return UFMError(@"UnsupportedFormat",@"ImageIO cannot read this image.");
    NSDictionary *options=@{ (id)kCGImageSourceCreateThumbnailFromImageAlways:@YES, (id)kCGImageSourceCreateThumbnailWithTransform:@YES,
        (id)kCGImageSourceThumbnailMaxPixelSize:job.request[@"edge"], (id)kCGImageSourceShouldCacheImmediately:@YES };
    CGImageRef image=CGImageSourceCreateThumbnailAtIndex(imageSource,0,(__bridge CFDictionaryRef)options); CFRelease(imageSource);
    if(!image) return UFMError(@"UnsupportedFormat",@"ImageIO thumbnail decoding failed.");
    NSString *path=[job.request[@"output"] stringByAppendingPathComponent:@"preview.png"];
    CGImageDestinationRef destination=CGImageDestinationCreateWithURL((__bridge CFURLRef)[NSURL fileURLWithPath:path],(__bridge CFStringRef)UTTypePNG.identifier,1,NULL);
    BOOL ok=NO; if(destination) { CGImageDestinationAddImage(destination,image,NULL); ok=CGImageDestinationFinalize(destination); CFRelease(destination); }
    CGImageRelease(image);
    return ok ? @{ @"status":@"ok", @"items":@[UFMFile(path,nil)] } : UFMError(@"WriteFailed",@"PNG encode failed.");
}

@interface UFMPicker : NSObject<PHPickerViewControllerDelegate,UIAdaptivePresentationControllerDelegate>
@property(nonatomic,strong) UFMJob *job;
@property(nonatomic) BOOL returned;
@end
@implementation UFMPicker
- (void)presentationControllerDidDismiss:(UIPresentationController *)controller {
    if(!self.returned) { self.returned=YES; UFMComplete(self.job,@{@"status":@"canceled"}); }
    UFMWindow=nil; UFMPresented=nil; UFMPickerDelegate=nil;
}
- (void)picker:(PHPickerViewController *)picker didFinishPicking:(NSArray<PHPickerResult*> *)results {
    if(self.returned) return; self.returned=YES; UFMJob *job=self.job;
    [picker dismissViewControllerAnimated:YES completion:^{ if([UFMWindow isEqual:job.identifier]) { UFMWindow=nil; UFMPresented=nil; UFMPickerDelegate=nil; } }];
    if(results.count==0) { UFMComplete(job,@{@"status":@"canceled"}); return; }
    if(results.count>[job.request[@"count"] unsignedIntegerValue]) { UFMComplete(job,UFMError(@"SelectionLimitExceeded",@"Selection exceeds requested limit.")); return; }
    [UFMQueue addOperationWithBlock:^{ @autoreleasepool {
        NSMutableArray *items=[NSMutableArray new];
        for(PHPickerResult *result in results) {
            if(job.canceled) { UFMComplete(job,@{@"status":@"canceled"}); return; }
            NSItemProvider *provider=result.itemProvider; NSString *type=nil;
            for(NSString *identifier in provider.registeredTypeIdentifiers) if([[UTType typeWithIdentifier:identifier] conformsToType:UTTypeImage]) { type=identifier; break; }
            if(!type) { UFMComplete(job,UFMError(@"UnsupportedFormat",@"Provider has no static image representation.")); return; }
            dispatch_semaphore_t semaphore=dispatch_semaphore_create(0); __block NSError *failure=nil; __block NSString *path=nil; __block NSString *name=nil;
            [provider loadFileRepresentationForTypeIdentifier:type completionHandler:^(NSURL *url,NSError *error) {
                failure=error;
                if(url && !error) {
                    name=url.lastPathComponent;
                    path=[job.request[@"output"] stringByAppendingPathComponent:[NSString stringWithFormat:@"%lu.%@",(unsigned long)items.count,url.pathExtension]];
                    [[NSFileManager defaultManager] copyItemAtURL:url toURL:[NSURL fileURLWithPath:path] error:&failure];
                }
                dispatch_semaphore_signal(semaphore);
            }];
            dispatch_semaphore_wait(semaphore,DISPATCH_TIME_FOREVER);
            if(failure || !path) { UFMComplete(job,UFMError(@"ReadFailed",failure.description ?: @"No provider file.")); return; }
            [items addObject:UFMFile(path,name)];
        }
        UFMComplete(job,@{@"status":@"ok",@"items":items});
    }}];
}
@end

@interface UFMFolderPicker : NSObject<UIDocumentPickerDelegate,UIAdaptivePresentationControllerDelegate>
@property(nonatomic,strong) UFMJob *job;
@property(nonatomic) BOOL returned;
@end
@implementation UFMFolderPicker
- (void)finish:(NSDictionary*)response {
    if(self.returned) return; self.returned=YES;
    UFMComplete(self.job,response);
    UIViewController *controller=UFMPresented;
    NSString *identifier=self.job.identifier;
    [controller dismissViewControllerAnimated:YES completion:^{ if([UFMWindow isEqual:identifier]) { UFMWindow=nil; UFMPresented=nil; UFMPickerDelegate=nil; } }];
}
- (void)documentPicker:(UIDocumentPickerViewController*)controller didPickDocumentsAtURLs:(NSArray<NSURL*>*)urls {
    NSURL *url=urls.firstObject; if(!url) { [self finish:UFMError(@"InvalidResult",@"No directory selected.")]; return; }
    if(![url startAccessingSecurityScopedResource]) { [self finish:UFMError(@"PermissionDenied",@"Cannot access selected directory.")]; return; }
    NSError *error=nil; NSData *bookmark=[url bookmarkDataWithOptions:0 includingResourceValuesForKeys:nil relativeToURL:nil error:&error];
    [url stopAccessingSecurityScopedResource];
    [self finish:bookmark ? @{@"status":@"ok",@"items":@[@{@"id":[bookmark base64EncodedStringWithOptions:0]}]} : UFMError(@"DirectoryAccessFailed",error.description)];
}
- (void)documentPickerWasCancelled:(UIDocumentPickerViewController*)controller { [self finish:@{@"status":@"canceled"}]; }
- (void)presentationControllerDidDismiss:(UIPresentationController*)controller {
    if(!self.returned) { self.returned=YES; UFMComplete(self.job,@{@"status":@"canceled"}); }
    UFMWindow=nil; UFMPresented=nil; UFMPickerDelegate=nil;
}
@end

static UIViewController *UFMController() {
    for(UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
        if(scene.activationState!=UISceneActivationStateForegroundActive || ![scene isKindOfClass:UIWindowScene.class]) continue;
        for(UIWindow *window in ((UIWindowScene*)scene).windows) if(window.isKeyWindow) return window.rootViewController;
    }
    // Unity projects that do not adopt UIScene still have an application key window.
    for(UIWindow *window in UIApplication.sharedApplication.windows) if(window.isKeyWindow) return window.rootViewController;
    return nil;
}
extern "C" void UFMStart(const char *json) {
    UFMInitialize(); NSData *data=[[NSString stringWithUTF8String:json] dataUsingEncoding:NSUTF8StringEncoding];
    UFMJob *job=[UFMJob new]; job.request=[NSJSONSerialization JSONObjectWithData:data options:0 error:nil]; job.identifier=job.request[@"id"];
    @synchronized(UFMJobs) { UFMJobs[job.identifier]=job; }
    dispatch_async(dispatch_get_main_queue(), ^{
        NSString *op=job.request[@"op"];
        if(job.canceled) { UFMComplete(job,@{@"status":@"canceled"}); return; }
        if([op isEqual:@"pickDirectory"]) {
            UIViewController *controller=UFMController();
            if(UFMWindow || !controller || controller.presentedViewController) { UFMComplete(job,UFMError(@"PickerBusy",@"Cannot present directory picker.")); return; }
            UFMWindow=job.identifier;
            UIDocumentPickerViewController *picker=[[UIDocumentPickerViewController alloc] initForOpeningContentTypes:@[UTTypeFolder] asCopy:NO];
            UFMFolderPicker *delegate=[UFMFolderPicker new]; delegate.job=job; picker.delegate=delegate;
            UFMPresented=picker; UFMPickerDelegate=delegate;
            [controller presentViewController:picker animated:YES completion:^{picker.presentationController.delegate=delegate;}];
        } else if([op isEqual:@"pick"]) {
            UIViewController *controller=UFMController();
            if(UFMWindow || !controller || controller.presentedViewController) { UFMComplete(job,UFMError(@"PickerBusy",@"Cannot present another photo picker.")); return; }
            UFMWindow=job.identifier; PHPickerConfiguration *config=[[PHPickerConfiguration alloc] init]; config.filter=PHPickerFilter.imagesFilter;
            config.selectionLimit=[job.request[@"count"] integerValue]; config.preferredAssetRepresentationMode=PHPickerConfigurationAssetRepresentationModeCurrent;
            PHPickerViewController *picker=[[PHPickerViewController alloc] initWithConfiguration:config]; UFMPicker *delegate=[UFMPicker new]; delegate.job=job;
            picker.delegate=delegate; UFMPickerDelegate=delegate; UFMPresented=picker;
            [controller presentViewController:picker animated:YES completion:^{ picker.presentationController.delegate=delegate; }];
        } else if([op isEqual:@"access"]) UFMComplete(job,@{@"status":@"ok",@"access":UFMAccess()});
        else if([op isEqual:@"requestAccess"]) {
            if(![[NSBundle mainBundle] objectForInfoDictionaryKey:@"NSPhotoLibraryUsageDescription"]) { UFMComplete(job,UFMError(@"InvalidConfiguration",@"Photo library usage description is missing.")); return; }
            [PHPhotoLibrary requestAuthorizationForAccessLevel:PHAccessLevelReadWrite handler:^(PHAuthorizationStatus status) { UFMComplete(job,@{@"status":@"ok",@"access":UFMAccess()}); }];
        } else [UFMQueue addOperationWithBlock:^{ @autoreleasepool {
            if([op isEqual:@"albums"] || [op isEqual:@"images"]) UFMComplete(job,UFMLibrary(job));
            else if([op isEqual:@"directory"]) UFMComplete(job,UFMDirectory(job));
            else if([op isEqual:@"export"] || [op isEqual:@"preview"]) UFMComplete(job,UFMRead(job));
            else UFMComplete(job,UFMError(@"UnsupportedOperation",op));
        }}];
    });
}
extern "C" char *UFMPoll(const char *identifier) {
    UFMInitialize(); NSString *key=[NSString stringWithUTF8String:identifier];
    @synchronized(UFMJobs) { UFMJob *job=UFMJobs[key]; if(!job.finished) return NULL; char *result=strdup(job.result.UTF8String); [UFMJobs removeObjectForKey:key]; return result; }
}
extern "C" void UFMFree(void *value) { free(value); }
extern "C" void UFMCancel(const char *identifier) {
    UFMInitialize(); NSString *key=[NSString stringWithUTF8String:identifier]; __block UFMJob *job;
    @synchronized(UFMJobs) { job=UFMJobs[key]; job.canceled=YES; if(job.finished) { UFMClean(job); [UFMJobs removeObjectForKey:key]; } }
    dispatch_async(dispatch_get_main_queue(), ^{
        if([UFMWindow isEqual:key] && UFMPresented) {
            UFMPicker *delegate=UFMPickerDelegate;
            BOOL importing=delegate.returned; delegate.returned=YES;
            [UFMPresented dismissViewControllerAnimated:YES completion:^{ if([UFMWindow isEqual:key]) { UFMWindow=nil; UFMPresented=nil; UFMPickerDelegate=nil; } if(!importing) UFMComplete(job,@{@"status":@"canceled"}); }];
        }
    });
}
