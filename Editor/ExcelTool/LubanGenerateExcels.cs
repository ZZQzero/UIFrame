using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UIFrame.Editor
{
    public class LubanGenerateExcels
    {
        static bool running;

        [MenuItem("GameTool/Excel/ExcelExporter")]
        public static async void GenerateExcelTools()
        {
            if (running)
            {
                Debug.LogError("[UIFrame] Excel 生成正在进行，不能重复启动。");
                return;
            }

            running = true;
            try
            {
                var root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Config");
                await RunGeneratorAsync(root);
                Debug.Log("Gen Excel Success");
                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                // MenuItem 没有等待方，必须将完整错误交给 Editor Console。
                Debug.LogException(exception);
            }
            finally
            {
                running = false;
            }
        }

        internal static async Task RunGeneratorAsync(string root)
        {
#if UNITY_EDITOR_WIN
            var script = Path.Combine(root, "gen.bat");
            var executable = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            var arguments = "/d /s /c \"\"" + script + "\"\"";
#elif UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            var script = Path.Combine(root, "gen.sh");
            var executable = "/bin/bash";
            var arguments = "\"" + script.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
#else
            throw new PlatformNotSupportedException("Excel 生成器仅支持 Windows、macOS 和 Linux Editor。");
#endif
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            if (!File.Exists(script))
                throw new FileNotFoundException("找不到 Excel 生成脚本。", script);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    WorkingDirectory = root,
                    FileName = executable,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            if (!process.Start())
                throw new InvalidOperationException($"无法启动 Excel 生成器: {script}");

            // 同时读取两个管道，避免输出写满导致子进程与等待方互锁。
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit());
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Excel 生成失败: Script={script}, ExitCode={process.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");
#endif
        }
    }
}
