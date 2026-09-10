using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UIFrame.Editor
{
    public class LubanGenerateExcels
    {
        static string projectPath = "Assets";

        [MenuItem("GameTool/Excel/ExcelExporter")]
        public static void GenerateExcelTools()
        {
            string path = Application.dataPath;

            path = path.Replace(projectPath, "");
            path = Path.Combine(path, "Config");
            string rootPath = path;

            Process process = new Process();
            process.StartInfo.WorkingDirectory = rootPath;
#if UNITY_EDITOR_WIN
            path = Path.Combine(rootPath, "gen.bat");
#elif UNITY_EDITOR_OSX
		path = Path.Combine(rootPath, "gen.sh");
#endif
            process.StartInfo.FileName = path;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();
            process.WaitForExit();

            int exitCode = process.ExitCode;

            if (exitCode == 0)
            {
                Debug.Log("Gen Excel Success");
                AssetDatabase.Refresh();
            }
            else
            {
                Debug.LogError("Gen Excel Failed!!!! 【报错看下一行】");
                Debug.LogError(process.StandardOutput.ReadToEnd());
            }

            process.Close();
        }
    }
}