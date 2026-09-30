"""Run the iOS repository's ownership tests on macOS Foundation, without OS uploads."""
from pathlib import Path
import subprocess
import tempfile


def main():
    source = Path(__file__).with_name("backup_persistence.mm")
    with tempfile.TemporaryDirectory(prefix="uiframe-native-backup-") as temporary:
        root = Path(temporary)
        (root / "UIKit").mkdir()
        (root / "UIKit/UIKit.h").write_text("#import <Foundation/Foundation.h>\n")
        (root / "PluginBase").mkdir()
        (root / "PluginBase/AppDelegateListener.h").write_text(
            "#import <Foundation/Foundation.h>\n"
            "@protocol AppDelegateListener <NSObject>\n@end\n"
            "static void UnityRegisterAppDelegateListener(id object) {}\n"
        )
        executable = root / "backup_persistence"
        subprocess.run([
            "xcrun", "clang++", "-fobjc-arc", "-fblocks", "-std=c++17",
            "-framework", "Foundation", "-framework", "Security",
            "-I", str(root), str(source), "-o", str(executable),
        ], check=True)
        store = root / "store"
        store.mkdir()
        subprocess.run([str(executable), str(store)], check=True)


if __name__ == "__main__":
    main()
