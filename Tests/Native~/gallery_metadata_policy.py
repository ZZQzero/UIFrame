"""Exercise production metadata/access classifiers on the host, without mobile providers."""
from pathlib import Path
import argparse
import subprocess
import tempfile

PACKAGE = Path(__file__).resolve().parents[2]


def function(source, declaration):
    start = source.index(declaration)
    end = source.index('{', start)
    depth = 1
    while depth:
        end += 1
        depth += (source[end] == '{') - (source[end] == '}')
    return source[start:end+1]


def main():
    parser=argparse.ArgumentParser();parser.add_argument("--jdk",type=Path);args=parser.parse_args()
    def java_tool(name): return str(args.jdk/"bin"/name) if args.jdk else name
    android = (PACKAGE / 'Runtime/Plugins/Android/UIFrameGallery.androidlib/src/main/java/com/zzq/uiframe/media/GalleryIndex.java').read_text()
    ios = (PACKAGE / 'Runtime/Plugins/iOS/UIFrameGallery.mm').read_text()
    with tempfile.TemporaryDirectory(prefix='uiframe-gallery-policy-') as directory:
        root = Path(directory)
        java = '''public class GalleryVersionPolicy {
    static class Cursor {
        Long[] values; Cursor(Long modified,Long size){values=new Long[]{modified,size};}
        boolean isNull(int column){return values[column]==null;}
        long getLong(int column){return values[column];}
    }
    HELPER
    public static void main(String[] args) {
        for(Long[] pair:new Long[][]{{null,null},{null,4096L},{123L,null},{0L,4096L},{123L,-1L}})
            if(documentVersion(new Cursor(pair[0],pair[1]),0,1)!=null)throw new AssertionError("Unknown version accepted");
        if(!"123:4096".equals(documentVersion(new Cursor(123L,4096L),0,1)))throw new AssertionError("Valid version lost");
        if(!"123:0".equals(documentVersion(new Cursor(123L,0L),0,1)))throw new AssertionError("Empty document lost");
        System.out.println("Production SAF version classifier: 7 cases passed");
    }
}'''.replace('HELPER', function(android, '    static String documentVersion('))
        (root/'GalleryVersionPolicy.java').write_text(java)
        subprocess.run([java_tool('javac'), str(root/'GalleryVersionPolicy.java')], check=True)
        subprocess.run([java_tool('java'), '-cp', str(root), 'GalleryVersionPolicy'], check=True)
        helpers = '\n'.join(function(ios, name) for name in (
            'static NSDictionary *UFMError(', 'static NSString *UFMAccessErrorCode(',
            'static NSError *UFMScopeFailure(', 'static NSDictionary *UFMSourceError(',
            'static NSDictionary *UFMPhotoAccessFailure(', 'static NSDictionary *UFMPhotoSourceFailure('))
        objc = r'''
#import <Foundation/Foundation.h>
#import <Photos/Photos.h>
#include <errno.h>
@interface UFMJob : NSObject
@property NSDictionary *request;
@end
@implementation UFMJob
@end
static NSString *accessState=@"Authorized";
static NSString *UFMAccess(){return accessState;}
HELPERS
static void check(BOOL value){if(!value)abort();}
int main(){@autoreleasepool{
    UFMJob *job=[UFMJob new];job.request=@{@"source":@"directory",@"op":@"stat"};
    NSError *denied=[NSError errorWithDomain:NSPOSIXErrorDomain code:EACCES userInfo:nil];
    NSError *missing=[NSError errorWithDomain:NSCocoaErrorDomain code:NSFileReadNoSuchFileError userInfo:nil];
    check([UFMSourceError(job,denied,@"ReadFailed")[@"code"] isEqual:@"PermissionDenied"]);
    check([UFMSourceError(job,missing,@"ReadFailed")[@"code"] isEqual:@"ReadFailed"]);
    check([UFMSourceError(job,UFMScopeFailure(2,@"stale",nil),@"ReadFailed")[@"code"] isEqual:@"ScopeConfirmationRequired"]);
    job.request=@{@"source":@"file"};
    check([UFMSourceError(job,denied,@"SourceUnavailable")[@"code"] isEqual:@"SourceUnavailable"]);
    for(NSString *access in @[@"Authorized",@"Limited"]){
        accessState=access;check(UFMPhotoAccessFailure(nil)==nil);
        check([UFMPhotoSourceFailure(nil)[@"code"] isEqual:@"SourceUnavailable"]);
        NSError *network=[NSError errorWithDomain:NSURLErrorDomain code:NSURLErrorNotConnectedToInternet userInfo:nil];
        check([UFMPhotoSourceFailure(network)[@"code"] isEqual:@"SourceUnavailable"]);
        for(NSNumber *code in @[@(PHPhotosErrorAccessUserDenied),@(PHPhotosErrorAccessRestricted)]){
            NSError *error=[NSError errorWithDomain:PHPhotosErrorDomain code:code.integerValue userInfo:nil];
            check([UFMPhotoSourceFailure(error)[@"code"] isEqual:@"PermissionDenied"]);
            NSError *wrapped=[NSError errorWithDomain:@"Provider" code:1 userInfo:@{NSUnderlyingErrorKey:error}];
            check([UFMPhotoSourceFailure(wrapped)[@"code"] isEqual:@"PermissionDenied"]);
        }
    }
    for(NSString *access in @[@"Denied",@"Restricted",@"NotDetermined"]){
        accessState=access;check([UFMPhotoSourceFailure(nil)[@"code"] isEqual:@"PermissionDenied"]);
    }
    puts("Production iOS access classifiers passed: denial, restoration, Limited, deletion, network, bookmark and local-file boundaries");
}}
'''.replace('HELPERS', helpers)
        (root/'access.mm').write_text(objc)
        subprocess.run(['xcrun','clang++','-fobjc-arc','-framework','Foundation','-framework','Photos',str(root/'access.mm'),'-o',str(root/'access')], check=True)
        subprocess.run([str(root/'access')], check=True)


if __name__ == '__main__':
    main()
