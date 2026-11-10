using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace BoardSandbox.CoreTests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            int passed = 0;
            int failed = 0;
            Stopwatch total = Stopwatch.StartNew();

            IEnumerable<Type> fixtures = Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => t.GetCustomAttribute<TestFixtureAttribute>() != null)
                .OrderBy(t => t.FullName, StringComparer.Ordinal);

            foreach (Type fixture in fixtures)
            {
                List<MethodInfo> methods = fixture.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.GetCustomAttribute<TestAttribute>() != null || m.GetCustomAttributes<TestCaseAttribute>().Any())
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ToList();
                if (methods.Count == 0)
                    continue;

                MethodInfo[] oneTimeSetUp = Marked<OneTimeSetUpAttribute>(fixture);
                MethodInfo[] oneTimeTearDown = Marked<OneTimeTearDownAttribute>(fixture);
                MethodInfo[] setUp = Marked<SetUpAttribute>(fixture);
                MethodInfo[] tearDown = Marked<TearDownAttribute>(fixture);

                object instance = Activator.CreateInstance(fixture);
                bool fixtureReady = true;
                try
                {
                    foreach (MethodInfo m in oneTimeSetUp)
                        m.Invoke(instance, null);
                }
                catch (Exception e)
                {
                    fixtureReady = false;
                    failed++;
                    Console.WriteLine("FAIL " + fixture.Name + " (one-time setup): " + Unwrap(e));
                }

                if (fixtureReady)
                {
                    foreach (MethodInfo method in methods)
                    {
                        List<object[]> cases = method.GetCustomAttributes<TestCaseAttribute>().Select(c => c.Arguments).ToList();
                        if (cases.Count == 0)
                            cases.Add(new object[0]);
                        foreach (object[] arguments in cases)
                        {
                            string name = fixture.Name + "." + method.Name + (arguments.Length > 0 ? "(" + string.Join(", ", arguments.Select(a => a == null ? "null" : a.ToString())) + ")" : "");
                            if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            Stopwatch clock = Stopwatch.StartNew();
                            try
                            {
                                foreach (MethodInfo m in setUp)
                                    m.Invoke(instance, null);
                                try
                                {
                                    method.Invoke(instance, arguments);
                                }
                                finally
                                {
                                    foreach (MethodInfo m in tearDown)
                                        m.Invoke(instance, null);
                                }
                                passed++;
                                Console.WriteLine("ok   " + name + " (" + clock.ElapsedMilliseconds + " ms)");
                            }
                            catch (Exception e)
                            {
                                failed++;
                                Console.WriteLine("FAIL " + name + ": " + Unwrap(e));
                            }
                        }
                    }
                }

                try
                {
                    foreach (MethodInfo m in oneTimeTearDown)
                        m.Invoke(instance, null);
                }
                catch (Exception e)
                {
                    failed++;
                    Console.WriteLine("FAIL " + fixture.Name + " (one-time teardown): " + Unwrap(e));
                }
            }

            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failed + " failed in " + total.Elapsed.TotalSeconds.ToString("0.0") + " s");
            return failed == 0 ? 0 : 1;
        }

        private static MethodInfo[] Marked<T>(Type fixture) where T : Attribute
        {
            return fixture.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<T>() != null).ToArray();
        }

        private static string Unwrap(Exception e)
        {
            Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            return inner.GetType().Name + ": " + inner.Message + (inner is AssertionException ? string.Empty : "\n" + inner.StackTrace);
        }
    }
}
