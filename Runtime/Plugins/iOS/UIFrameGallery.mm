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
@property(nonatomic,strong) NSArray *pages;
@property(nonatomic) NSUInteger offset;
@property(nonatomic) PHImageRequestID imageRequest;
@property(nonatomic) BOOL imagePending;
@property(atomic) BOOL canceled;
@property(nonatomic) BOOL finished;
@property(nonatomic,strong) NSURL *securityRoot;
@property(nonatomic,strong) NSProgress *providerProgress;
@property(nonatomic,strong) NSOutputStream *resourceOutput;
@property(nonatomic) PHAssetResourceDataRequestID resourceRequest;
@property(nonatomic) BOOL resourcePending;
@property(nonatomic) BOOL copying;
@property(nonatomic) NSUInteger providerGeneration;
@property(nonatomic,strong) NSDictionary *resourceFailure;
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
        else if ([result[@"status"] isEqual:@"ok"] && [@[@"images",@"albums",@"directory"] containsObject:job.request[@"op"]]) job.pages=result[@"items"];
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
    *error=UFMFailure(@"Unsupported local image source."); return nil;
}
static NSDictionary *UFMCopyImage(UFMJob *job, NSString *source, NSString *destination) {
    NSInputStream *input=[NSInputStream inputStreamWithFileAtPath:source];
    NSOutputStream *output=[NSOutputStream outputStreamToFileAtPath:destination append:NO];
    [input open]; [output open];
    @try {
        if (input.streamError) return UFMError(@"SourceUnavailable",input.streamError.description);
        if (output.streamError) return UFMError(@"WriteFailed",output.streamError.description);
        uint8_t buffer[131072]; long long total=0, limit=[job.request[@"maxBytes"] longLongValue];
        for (;;) {
            if (job.canceled) return @{@"status":@"canceled"};
            NSInteger count=[input read:buffer maxLength:sizeof(buffer)];
            if (count<0) return UFMError(@"SourceUnavailable",input.streamError.description);
            if (count==0) return nil;
            if (limit>0 && count>limit-total) return UFMError(@"SizeLimitExceeded",@"Image exceeds export byte limit.");
            for (NSInteger offset=0;offset<count;) {
                NSInteger written=[output write:buffer+offset maxLength:count-offset];
                if (written<=0) return UFMError(@"WriteFailed",output.streamError.description);
                offset+=written;
            }
            total+=count;
        }
    } @finally { [input close]; [output close]; }
}
static NSDictionary *UFMEncodeImage(UFMJob *job, CGImageRef image) {
    if (job.canceled) return @{@"status":@"canceled"};
    BOOL jpeg=[job.request[@"format"] isEqual:@"jpg"];
    CGImageRef flattened=NULL;
    if(jpeg) {
        size_t w=CGImageGetWidth(image), h=CGImageGetHeight(image);
        CGColorSpaceRef space=CGColorSpaceCreateWithName(kCGColorSpaceSRGB);
        CGContextRef context=CGBitmapContextCreate(NULL,w,h,8,0,space,kCGImageAlphaNoneSkipLast|kCGBitmapByteOrder32Big); CGColorSpaceRelease(space);
        if(!context) return UFMError(@"MemoryLimitExceeded",@"Cannot allocate JPEG composition buffer.");
        CGContextSetRGBFillColor(context,[job.request[@"backgroundR"] doubleValue],[job.request[@"backgroundG"] doubleValue],[job.request[@"backgroundB"] doubleValue],1);
        CGContextFillRect(context,CGRectMake(0,0,w,h)); CGContextDrawImage(context,CGRectMake(0,0,w,h),image);
        flattened=CGBitmapContextCreateImage(context); CGContextRelease(context);
        if(!flattened) return UFMError(@"MemoryLimitExceeded",@"Cannot compose JPEG image.");
        image=flattened;
    }
    NSString *path=[job.request[@"output"] stringByAppendingPathComponent:jpeg?@"image.jpg":@"preview.png"];
    CGImageDestinationRef destination=CGImageDestinationCreateWithURL((__bridge CFURLRef)[NSURL fileURLWithPath:path],(__bridge CFStringRef)(jpeg?UTTypeJPEG.identifier:UTTypePNG.identifier),1,NULL);
    NSDictionary *options=jpeg?@{(id)kCGImageDestinationLossyCompressionQuality:@([job.request[@"quality"] doubleValue]/100.0)}:nil;
    BOOL ok=NO; if(destination) { CGImageDestinationAddImage(destination,image,(__bridge CFDictionaryRef)options); ok=CGImageDestinationFinalize(destination); CFRelease(destination); }
    if(flattened) CGImageRelease(flattened);
    return ok ? @{@"status":@"ok",@"items":@[UFMFile(path,nil)]} : UFMError(@"WriteFailed",@"Image encoding failed.");
}
static NSDictionary *UFMReadSource(UFMJob *job, NSString *source) {
    if(job.canceled) return @{ @"status":@"canceled" };
    if([job.request[@"op"] isEqual:@"export"]) {
        NSString *path=[job.request[@"output"] stringByAppendingPathComponent:[@"image." stringByAppendingString:source.pathExtension]];
        if (![source isEqual:path]) {
            NSDictionary *failure=UFMCopyImage(job,source,path); if (failure) return failure;
        }
        return @{ @"status":@"ok", @"items":@[UFMFile(path,source.lastPathComponent)] };
    }
    CGImageSourceRef imageSource=CGImageSourceCreateWithURL((__bridge CFURLRef)[NSURL fileURLWithPath:source],NULL);
    if(!imageSource) return UFMError(@"UnsupportedFormat",@"ImageIO cannot read this image.");
    NSDictionary *options=@{ (id)kCGImageSourceCreateThumbnailFromImageAlways:@YES, (id)kCGImageSourceCreateThumbnailWithTransform:@YES,
        (id)kCGImageSourceThumbnailMaxPixelSize:job.request[@"edge"], (id)kCGImageSourceShouldCacheImmediately:@YES };
    CGImageRef image=CGImageSourceCreateThumbnailAtIndex(imageSource,0,(__bridge CFDictionaryRef)options); CFRelease(imageSource);
    if(!image) return UFMError(@"UnsupportedFormat",@"ImageIO thumbnail decoding failed.");
    NSDictionary *result=UFMEncodeImage(job,image); CGImageRelease(image); return result;
}

// PhotoKit selects an appropriately sized representation; previews never export the original resource.
static void UFMReadThumbnail(UFMJob *job) {
    PHAsset *asset=[PHAsset fetchAssetsWithLocalIdentifiers:@[job.request[@"path"]] options:nil].firstObject;
    if(!asset) { UFMComplete(job,UFMError(@"SourceUnavailable",@"Photo is no longer accessible.")); return; }
    PHImageRequestOptions *options=[PHImageRequestOptions new]; options.networkAccessAllowed=YES;
    options.deliveryMode=PHImageRequestOptionsDeliveryModeHighQualityFormat; options.resizeMode=PHImageRequestOptionsResizeModeExact;
    CGFloat edge=[job.request[@"edge"] doubleValue];
    @synchronized(job) { if(job.canceled) { UFMComplete(job,@{@"status":@"canceled"}); return; } job.imagePending=YES; }
    PHImageRequestID request=[[PHImageManager defaultManager] requestImageForAsset:asset targetSize:CGSizeMake(edge,edge) contentMode:PHImageContentModeAspectFit options:options resultHandler:^(UIImage *image,NSDictionary *info) {
        if([info[PHImageResultIsDegradedKey] boolValue]) return;
        @synchronized(job) { job.imagePending=NO; job.copying=YES; }
        if(job.canceled) { UFMComplete(job,@{@"status":@"canceled"}); return; }
        if(!image || info[PHImageErrorKey]) { UFMComplete(job,UFMError(@"SourceUnavailable",[info[PHImageErrorKey] description] ?: @"No photo preview returned.")); return; }
        [UFMQueue addOperationWithBlock:^{ @autoreleasepool {
            if(job.canceled) { UFMComplete(job,@{@"status":@"canceled"}); return; }
            CGFloat w=image.size.width, h=image.size.height, scale=MIN(1,edge/MAX(w,h));
            CGSize size=CGSizeMake(MAX(1,floor(w*scale)),MAX(1,floor(h*scale)));
            UIGraphicsImageRendererFormat *format=[UIGraphicsImageRendererFormat defaultFormat]; format.scale=1; format.opaque=NO;
            UIGraphicsImageRenderer *renderer=[[UIGraphicsImageRenderer alloc] initWithSize:size format:format];
            UIImage *normalized=[renderer imageWithActions:^(UIGraphicsImageRendererContext *context) { [image drawInRect:CGRectMake(0,0,size.width,size.height)]; }];
            UFMComplete(job,UFMEncodeImage(job,normalized.CGImage));
        }}];
    }];
    @synchronized(job) { job.imageRequest=request; if(job.canceled) [[PHImageManager defaultManager] cancelImageRequest:request]; }
}
// Cloud/provider waits hold no worker slot. Cancellation detaches their output before returning ownership.
static void UFMReadAsset(UFMJob *job) {
    PHAsset *asset=[PHAsset fetchAssetsWithLocalIdentifiers:@[job.request[@"path"]] options:nil].firstObject;
    if (!asset) { UFMComplete(job,UFMError(@"SourceUnavailable",@"Source is unavailable or permission was revoked.")); return; }
    PHAssetResource *resource=nil;
    for (PHAssetResource *candidate in [PHAssetResource assetResourcesForAsset:asset]) {
        if (candidate.type==PHAssetResourceTypeFullSizePhoto) { resource=candidate; break; }
        if (candidate.type==PHAssetResourceTypePhoto) resource=candidate;
    }
    if (!resource) { UFMComplete(job,UFMError(@"UnsupportedFormat",@"No static photo representation available.")); return; }
    NSString *path=[job.request[@"output"] stringByAppendingPathComponent:[@"image." stringByAppendingString:resource.originalFilename.pathExtension]];
    @synchronized(job) {
        if (job.canceled) { UFMComplete(job,@{@"status":@"canceled"}); return; }
        job.resourceOutput=[NSOutputStream outputStreamToFileAtPath:path append:NO]; [job.resourceOutput open];
        job.resourcePending=YES;
    }
    PHAssetResourceRequestOptions *options=[PHAssetResourceRequestOptions new]; options.networkAccessAllowed=YES;
    __block long long total=0; long long limit=[job.request[@"maxBytes"] longLongValue];
    PHAssetResourceDataRequestID request=[[PHAssetResourceManager defaultManager] requestDataForAssetResource:resource options:options dataReceivedHandler:^(NSData *data) {
        @synchronized(job) {
            if (job.canceled || job.resourceFailure) return;
            if (limit>0 && (long long)data.length>limit-total) job.resourceFailure=UFMError(@"SizeLimitExceeded",@"Image exceeds export byte limit.");
            else {
                const uint8_t *bytes=(const uint8_t*)data.bytes;
                for (NSUInteger offset=0;offset<data.length;) {
                    NSInteger count=[job.resourceOutput write:bytes+offset maxLength:data.length-offset];
                    if (count<=0) { job.resourceFailure=UFMError(@"WriteFailed",job.resourceOutput.streamError.description); break; }
                    offset+=count;
                }
                total+=data.length;
            }
        }
        if (job.resourceFailure) [[PHAssetResourceManager defaultManager] cancelDataRequest:job.resourceRequest];
    } completionHandler:^(NSError *error) {
        NSDictionary *failure;
        @synchronized(job) { [job.resourceOutput close]; job.resourceOutput=nil; job.resourcePending=NO; failure=job.resourceFailure; }
        if (job.canceled) { UFMComplete(job,@{@"status":@"canceled"}); return; }
        if (failure || error) { UFMComplete(job,failure ?: UFMError(@"SourceUnavailable",error.description)); return; }
        [UFMQueue addOperationWithBlock:^{ @autoreleasepool { UFMComplete(job,UFMReadSource(job,path)); } }];
    }];
    @synchronized(job) {
        job.resourceRequest=request;
        if (job.canceled || job.resourceFailure) [[PHAssetResourceManager defaultManager] cancelDataRequest:request];
    }
}
static void UFMImport(UFMJob *job, NSArray<PHPickerResult*> *results, NSUInteger index, NSMutableArray *items) {
    if (job.canceled) { UFMComplete(job,@{@"status":@"canceled"}); return; }
    if (index==results.count) { UFMComplete(job,@{@"status":@"ok",@"items":items}); return; }
    NSItemProvider *provider=results[index].itemProvider; NSString *type=nil;
    for (NSString *identifier in provider.registeredTypeIdentifiers)
        if ([[UTType typeWithIdentifier:identifier] conformsToType:UTTypeImage]) { type=identifier; break; }
    if (!type) { UFMComplete(job,UFMError(@"UnsupportedFormat",@"Provider has no static image representation.")); return; }
    NSUInteger generation;
    @synchronized(job) { generation=++job.providerGeneration; }
    NSProgress *progress=[provider loadFileRepresentationForTypeIdentifier:type completionHandler:^(NSURL *url,NSError *error) {
        @synchronized(job) { job.providerProgress=nil; job.copying=YES; }
        if (job.canceled) { UFMComplete(job,@{@"status":@"canceled"}); return; }
        if (!url || error) { UFMComplete(job,UFMError(@"SourceUnavailable",error.description ?: @"No provider file.")); return; }
        NSString *path=[job.request[@"output"] stringByAppendingPathComponent:[NSString stringWithFormat:@"%lu.%@",(unsigned long)index,url.pathExtension]];
        NSDictionary *failure=UFMCopyImage(job,url.path,path);
        @synchronized(job) { job.copying=NO; }
        if (failure) { UFMComplete(job,failure); return; }
        [items addObject:UFMFile(path,url.lastPathComponent)]; UFMImport(job,results,index+1,items);
    }];
    @synchronized(job) { if (!job.finished && !job.copying && job.providerGeneration==generation) job.providerProgress=progress; }
    if (job.canceled) [progress cancel];
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
    UFMImport(job,results,0,[NSMutableArray new]);
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
            else if([op isEqual:@"export"] || [op isEqual:@"preview"]) {
                if ([job.request[@"source"] isEqual:@"file"] || [job.request[@"source"] isEqual:@"directory"]) {
                    NSError *error=nil; NSString *source=UFMSource(job,&error);
                    UFMComplete(job,source ? UFMReadSource(job,source) : UFMError(@"SourceUnavailable",error.description));
                } else if([op isEqual:@"preview"]) UFMReadThumbnail(job);
                else UFMReadAsset(job);
            }
            else UFMComplete(job,UFMError(@"UnsupportedOperation",op));
        }}];
    });
}
extern "C" char *UFMPoll(const char *identifier) {
    UFMInitialize(); NSString *key=[NSString stringWithUTF8String:identifier];
    @synchronized(UFMJobs) {
        UFMJob *job=UFMJobs[key]; if(!job.finished) return NULL;
        if(job.pages) {
            NSUInteger end=MIN(job.pages.count,job.offset+200); BOOL more=end<job.pages.count;
            NSDictionary *page=@{@"status":@"ok",@"items":[job.pages subarrayWithRange:NSMakeRange(job.offset,end-job.offset)],@"more":@(more)};
            job.offset=end; if(!more) [UFMJobs removeObjectForKey:key];
            NSData *data=[NSJSONSerialization dataWithJSONObject:page options:0 error:nil]; return strdup([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String);
        }
        char *result=strdup(job.result.UTF8String); [UFMJobs removeObjectForKey:key]; return result;
    }
}
extern "C" void UFMFree(void *value) { free(value); }
extern "C" void UFMCancel(const char *identifier) {
    UFMInitialize(); NSString *key=[NSString stringWithUTF8String:identifier]; __block UFMJob *job;
    @synchronized(UFMJobs) { job=UFMJobs[key]; job.canceled=YES; if(job.finished) { UFMClean(job); [UFMJobs removeObjectForKey:key]; } }
    BOOL detached=NO;
    @synchronized(job) {
        if(job.imagePending) { [[PHImageManager defaultManager] cancelImageRequest:job.imageRequest]; job.imagePending=NO; detached=YES; }
        if (job.resourcePending) {
            [[PHAssetResourceManager defaultManager] cancelDataRequest:job.resourceRequest];
            [job.resourceOutput close]; job.resourceOutput=nil; detached=YES;
        }
        if (job.providerProgress && !job.copying) { [job.providerProgress cancel]; job.providerProgress=nil; detached=YES; }
    }
    if (detached) UFMComplete(job,@{@"status":@"canceled"});
    dispatch_async(dispatch_get_main_queue(), ^{
        if([UFMWindow isEqual:key] && UFMPresented) {
            UFMPicker *delegate=UFMPickerDelegate;
            BOOL importing=delegate.returned; delegate.returned=YES;
            [UFMPresented dismissViewControllerAnimated:YES completion:^{ if([UFMWindow isEqual:key]) { UFMWindow=nil; UFMPresented=nil; UFMPickerDelegate=nil; } if(!importing) UFMComplete(job,@{@"status":@"canceled"}); }];
        }
    });
}

extern "C" bool UFMPending(const char *identifier) {
    UFMInitialize(); @synchronized(UFMJobs) { return UFMJobs[[NSString stringWithUTF8String:identifier]]!=nil; }
}
