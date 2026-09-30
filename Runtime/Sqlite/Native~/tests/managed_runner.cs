using System;
using System.Linq;
using UIFrame.Sqlite;
using UIFrame.Sqlite.Tests;
class Runner
{
    static int Main()
    {
        int failed = 0, passed = 0;
        foreach (var method in typeof(SqliteTests)
                     .GetMethods()
                     .Where(m => m.GetCustomAttributes(typeof(NUnit.Framework.TestAttribute), false).Length !=
                                 0))
        {
            var fixture = new SqliteTests();
            try
            {
                fixture.Setup();
                method.Invoke(fixture, null);
                Console.WriteLine("PASS " + method.Name);
                passed++;
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL " + method.Name + " " + e);
                failed++;
            }
            finally
            {
                try
                {
                    fixture.Teardown();
                }
                catch (Exception e)
                {
                    Console.WriteLine("TEARDOWN " + e);
                    failed++;
                }
            }
        }
        SqliteRuntime.ShutdownAsync().GetAwaiter().GetResult();
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
