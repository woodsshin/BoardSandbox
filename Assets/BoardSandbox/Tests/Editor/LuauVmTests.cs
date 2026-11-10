using System;
using System.Collections.Generic;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Luau;
using BoardSandbox.Core.Mods;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class LuauVmTests
    {
        private const string Rules = @"
local rules = {}
function rules.new_game() bse.turn(1) end
function rules.command(seat, cmd)
    local a = cmd.action
    if a == 'spin' then while true do end
    elseif a == 'hog' then local t = {} for i = 1, 1e8 do t[i] = { i } end
    elseif a == 'log' then print('hello', 1) bse.warn('careful') bse.err('bad')
    end
end
function rules.query(name)
    if name == 'probe' then
        return { os = os ~= nil, io = io ~= nil, loadstring = loadstring ~= nil, dofile = dofile ~= nil, getfenv = getfenv ~= nil,
                 setglobal = pcall(function() leaked = 1 end), debug = type(debug) }
    end
    return nil
end
return rules";

        [Test]
        public void ReportsTheEmbeddedLuauVersion()
        {
            Assert.AreEqual("0.745", LuauVm.NativeLuauVersion);
        }

        [Test]
        public void BootsAndAnswersPing()
        {
            using (LuauVm vm = TestSupport.InlineMod(Rules).CreateVm())
            {
                JsonValue pong = vm.CallJson("ping");
                Assert.IsTrue(pong["ok"].AsBool());
                Assert.AreEqual("0.745", pong["luau"].AsString());
            }
        }

        [Test]
        public void SandboxHidesDangerousGlobals()
        {
            using (LuauVm vm = TestSupport.InlineMod(Rules).CreateVm())
            {
                JsonValue probe = vm.CallJson("query", JsonValue.NewObject().Set("name", "probe"))["value"];
                Assert.IsFalse(probe["os"].AsBool());
                Assert.IsFalse(probe["io"].AsBool());
                Assert.IsFalse(probe["loadstring"].AsBool());
                Assert.IsFalse(probe["dofile"].AsBool());
                Assert.IsFalse(probe["getfenv"].AsBool());
                Assert.IsFalse(probe["setglobal"].AsBool());
                Assert.AreEqual("table", probe["debug"].AsString());
            }
        }

        [Test]
        public void InfiniteLoopsTimeOutAndFaultTheVm()
        {
            using (LuauVm vm = TestSupport.InlineMod(Rules).CreateVm(new VmOptions { CallTimeoutMs = 80 }))
            {
                vm.CallJson("new_game");
                VmCallResult result = vm.Call("command", "{\"seat\":1,\"cmd\":{\"action\":\"spin\"}}");
                Assert.AreEqual(VmStatus.Timeout, result.Status);
                StringAssert.Contains("time limit", result.Text);
                Assert.IsTrue(vm.GetStats().Faulted);
                Assert.AreEqual(VmStatus.Faulted, vm.Call("ping").Status);
            }
        }

        [Test]
        public void MemoryHogsAreStopped()
        {
            using (LuauVm vm = TestSupport.InlineMod(Rules).CreateVm(new VmOptions { MemoryLimitMb = 8, CallTimeoutMs = 5000 }))
            {
                vm.CallJson("new_game");
                VmCallResult result = vm.Call("command", "{\"seat\":1,\"cmd\":{\"action\":\"hog\"}}");
                Assert.AreEqual(VmStatus.Memory, result.Status);
                Assert.IsTrue(vm.GetStats().Faulted);
            }
        }

        [Test]
        public void CollectsScriptOutputByLevel()
        {
            using (LuauVm vm = TestSupport.InlineMod(Rules).CreateVm())
            {
                vm.CallJson("new_game");
                vm.CallJson("command", JsonValue.Parse("{\"seat\":1,\"cmd\":{\"action\":\"log\"}}"));
                IReadOnlyList<LogLine> logs = vm.TakeLogs();
                Assert.AreEqual(3, logs.Count);
                Assert.AreEqual(0, logs[0].Level);
                Assert.AreEqual("hello\t1", logs[0].Message);
                Assert.AreEqual(1, logs[1].Level);
                Assert.AreEqual("careful", logs[1].Message);
                Assert.AreEqual(2, logs[2].Level);
                Assert.AreEqual(0, vm.TakeLogs().Count);
            }
        }

        [Test]
        public void MalformedArgumentsAreReportedWithoutFaultingTheVm()
        {
            using (LuauVm vm = TestSupport.InlineMod(Rules).CreateVm())
            {
                VmCallResult result = vm.Call("query", "{\"name\":");
                Assert.AreEqual(VmStatus.Json, result.Status);
                Assert.IsFalse(vm.GetStats().Faulted);
                Assert.AreEqual(VmStatus.Ok, vm.Call("ping").Status);
            }
        }

        [Test]
        public void ScriptsThatDoNotCompileFailToBoot()
        {
            ModPackage mod = TestSupport.InlineMod("local rules = {\nfunction (");
            LuauException e = Assert.Throws<LuauException>(() => mod.CreateVm());
            Assert.AreEqual(VmStatus.Compile, e.Status);
            StringAssert.Contains("main.luau", e.Message);
        }

        [Test]
        public void EntryModulesMustReturnATable()
        {
            LuauException e = Assert.Throws<LuauException>(() => TestSupport.InlineMod("return 5").CreateVm());
            StringAssert.Contains("table", e.Message);
        }

        [Test]
        public void RequireResolvesRelativeModules()
        {
            Dictionary<string, string> sources = new Dictionary<string, string>
            {
                { "main.luau", "local util = require('./lib/util') return { new_game = function() bse.turn(util.first) end, command = function() end }" },
                { "lib/util.luau", "return { first = 2 }" }
            };
            ModPackage mod = new ModPackage("{\"id\":\"req\",\"name\":\"Req\",\"api\":1,\"entry\":\"main.luau\"}", sources);
            using (LuauVm vm = mod.CreateVm())
            {
                JsonValue events = vm.CallJson("new_game")["events"];
                Assert.AreEqual("turn", events[1]["t"].AsString());
                Assert.AreEqual(2, events[1]["seat"].AsInt());
            }
        }

        [Test]
        public void DisposedVmsRefuseCalls()
        {
            LuauVm vm = TestSupport.InlineMod(Rules).CreateVm();
            vm.Dispose();
            Assert.IsTrue(vm.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => vm.Call("ping"));
            vm.Dispose();
        }

        [Test]
        public void StatsTrackCalls()
        {
            using (LuauVm vm = TestSupport.InlineMod(Rules).CreateVm())
            {
                long before = vm.GetStats().CallCount;
                vm.CallJson("ping");
                vm.CallJson("ping");
                VmStats stats = vm.GetStats();
                Assert.AreEqual(before + 2, stats.CallCount);
                Assert.Greater(stats.MemoryBytes, 0);
                Assert.GreaterOrEqual(stats.MemoryPeakBytes, stats.MemoryBytes);
            }
        }

        [Test]
        public void VmsAreIndependent()
        {
            using (LuauVm a = TestSupport.InlineMod(Rules).CreateVm(new VmOptions { CallTimeoutMs = 60 }))
            using (LuauVm b = TestSupport.InlineMod(Rules).CreateVm())
            {
                a.CallJson("new_game");
                b.CallJson("new_game");
                Assert.AreEqual(VmStatus.Timeout, a.Call("command", "{\"seat\":1,\"cmd\":{\"action\":\"spin\"}}").Status);
                Assert.AreEqual(VmStatus.Ok, b.Call("ping").Status);
            }
        }
    }
}
