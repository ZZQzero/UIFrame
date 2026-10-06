"""Run the production iOS response/deadline policy with macOS Foundation.

This checks the actual helper bodies, not NSURLSession background scheduling or
redirect delivery. Those behaviors still require an iOS device.
"""
from pathlib import Path
import subprocess
import tempfile


def main():
    source = (Path(__file__).resolve().parents[2] / 'Runtime/Plugins/iOS/UIFrameBackup.mm').read_text()
    helpers = []
    for declaration in ('static long long UFBNow(', 'static BOOL UFBControlExpired(', 'static NSString *UFBResponseFailure('):
        start = source.index(declaration)
        end = source.index('{', start)
        depth = 1
        while depth:
            end += 1
            depth += (source[end] == '{') - (source[end] == '}')
        helpers.append(source[start:end + 1])
    test = r'''
#import <Foundation/Foundation.h>
HELPERS
static void check(BOOL success) { if(!success)abort(); }
int main() { @autoreleasepool {
    NSURLRequest *request=[NSURLRequest requestWithURL:[NSURL URLWithString:@"https://storage.invalid/photo?signature=one"]];
    for(NSString *target in @[@"https://storage.invalid/photo?signature=one",
                              @"https://storage.invalid/other?signature=one",
                              @"https://another.invalid/photo?signature=one",
                              @"https://storage.invalid/photo?signature=two"]) {
        NSURLResponse *response=[[NSURLResponse alloc] initWithURL:[NSURL URLWithString:target] MIMEType:nil expectedContentLength:0 textEncodingName:nil];
        check((UFBResponseFailure(request,response)==nil)==[target isEqual:request.URL.absoluteString]);
    }
    check(UFBResponseFailure(request,nil)!=nil);
    check(UFBResponseFailure(nil,nil)!=nil);
    check(!UFBControlExpired(@{@"deadline_utc":@0}));
    check(!UFBControlExpired(@{@"deadline_utc":@(UFBNow()+600000000)}));
    check(UFBControlExpired(@{@"deadline_utc":@(UFBNow()-1)}));
    puts("Production iOS target and deadline helpers passed with Foundation; background transport not exercised");
} }
'''.replace('HELPERS', '\n'.join(helpers))
    with tempfile.TemporaryDirectory(prefix='ufbackup-ios-policy-') as directory:
        root = Path(directory)
        (root / 'policy.mm').write_text(test)
        subprocess.run(['xcrun', 'clang++', '-fobjc-arc', '-framework', 'Foundation',
                        str(root / 'policy.mm'), '-o', str(root / 'policy')], check=True)
        subprocess.run([str(root / 'policy')], check=True)


if __name__ == '__main__':
    main()
