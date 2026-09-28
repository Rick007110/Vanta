using Vanta.Core;
using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

public class SessionTests
{
    private static (TrainerSession s, FakeProcess p, byte[] orig, GameDef g) Make()
    {
        var g = TestUtil.Tlc();
        var (p, orig, _) = TestUtil.TlcProcess(g);
        var s = new TrainerSession(g, p);
        s.Runtime.FreeDelay = TimeSpan.Zero;
        return (s, p, orig, g);
    }

    [Fact]
    public void Enabling_all_toggles_then_restore_all_is_byte_identical()
    {
        var (s, p, orig, g) = Make();
        foreach (var c in g.Cheats.Where(c => c.Type == "toggle" && !c.Hidden)) s.Enable(c.Id);
        Assert.True(s.IsActive("fuel_hook"));             // hidden requirement auto-enabled for inf_petrol
        s.RestoreAll();
        Assert.Empty(s.ActiveIds);
        Assert.Equal(orig, p.Peek(TestUtil.ModBase, orig.Length));
        Assert.Equal(0, p.AllocatedCount);
    }

    [Fact]
    public void Hidden_requirement_goes_away_with_its_last_user()
    {
        var (s, _, _, _) = Make();
        s.Enable("inf_petrol"); s.Enable("inf_charge");
        s.Disable("inf_petrol");
        Assert.True(s.IsActive("fuel_hook"));
        s.Disable("inf_charge");
        Assert.False(s.IsActive("fuel_hook"));
    }

    [Fact]
    public void Petrol_toggle_writes_the_flag_in_the_cave_and_restores_it()
    {
        var (s, p, _, _) = Make();
        s.Enable("inf_petrol");
        var flags = s.Runtime.Symbols["fuel_flags"];
        Assert.Equal(1, p.Peek(flags, 1)[0]);
        s.Enable("inf_charge");
        Assert.Equal(1, p.Peek(flags + 1, 1)[0]);
        s.Disable("inf_petrol");
        Assert.Equal(0, p.Peek(flags, 1)[0]);
    }

    [Fact]
    public void Visible_requirement_is_not_auto_enabled_and_the_hint_is_the_error()
    {
        var (s, _, _, g) = Make();
        var e = Assert.Throws<CheatException>(() => s.SetValue("health_current", 50));
        Assert.Equal(g.Cheats.First(c => c.Id == "health_current").Hint, e.Message);
    }

    [Fact]
    public void Xp_value_through_exported_symbol_set_add_clamp_and_polling()
    {
        var (s, p, _, _) = Make();
        s.Enable("xp_hook");
        var events = new List<CheatStateDto>();
        s.Changed += l => events.AddRange(l);
        s.Tick();
        Assert.Contains(events, e => e.Id == "xp_value" && e.Hint != null && e.Value == null);   // xp pointer still 0
        Assert.Contains(events, e => e.Id == "xp_mult" && e.Value == 1);                         // dd 1 in the cave
        // simulate the game running through the hook: [xp_data] = address of the xp int
        p.AddRegion(0x300000000, new byte[64]);
        BitConverter.GetBytes(0x300000010UL).CopyTo(new byte[8], 0);
        p.Write(s.Runtime.Symbols["xp_data"], BitConverter.GetBytes(0x300000010UL));
        p.Write(0x300000010, BitConverter.GetBytes(1500));
        events.Clear(); s.Tick();
        Assert.Contains(events, e => e.Id == "xp_value" && e.Value == 1500);
        events.Clear(); s.Tick();
        Assert.Empty(events);                                                                    // only changes are emitted
        Assert.Equal(2500, s.Add("xp_value", 1000));
        Assert.Equal(2500, BitConverter.ToInt32(p.Peek(0x300000010, 4)));
        Assert.Equal(10, s.SetValue("xp_mult", 99));                                             // clamped to max
        Assert.Equal(10, BitConverter.ToInt32(p.Peek(s.Runtime.Symbols["xp_data"] + 8, 4)));
        s.RestoreAll(resetValues: true);
        Assert.False(s.IsActive("xp_hook"));
    }

    [Fact]
    public void Freeze_rewrites_the_value_on_tick()
    {
        var p = new FakeProcess(); p.AddModule("g.exe", TestUtil.ModBase, new byte[0x1000]);
        var g = new GameDef { Id = "g", Name = "G", ProcessNames = { "g.exe" }, Cheats =
        {
            new CheatDef { Id = "hp", Name = "HP", Type = "number", Impl = new ImplDef { Type = "pointer", Base = System.Text.Json.JsonDocument.Parse("\"g.exe+100\"").RootElement.Clone(), ValueType = "float", Freeze = true } },
            new CheatDef { Id = "god", Name = "God", Type = "toggle", Impl = new ImplDef { Type = "pointer", Base = System.Text.Json.JsonDocument.Parse("\"g.exe+200\"").RootElement.Clone(), ValueType = "byte", OnValue = 1, Freeze = true } },
        } };
        var s = new TrainerSession(g, p);
        s.SetValue("hp", 100);
        p.Write(TestUtil.ModBase + 0x100, BitConverter.GetBytes(3f));
        s.Tick();
        Assert.Equal(100f, BitConverter.ToSingle(p.Peek(TestUtil.ModBase + 0x100, 4)));
        p.Write(TestUtil.ModBase + 0x200, new byte[] { 7 });
        s.Enable("god");
        p.Write(TestUtil.ModBase + 0x200, new byte[] { 0 });
        s.Tick();
        Assert.Equal(1, p.Peek(TestUtil.ModBase + 0x200, 1)[0]);
        s.Disable("god");
        Assert.Equal(7, p.Peek(TestUtil.ModBase + 0x200, 1)[0]);                                 // original value restored
    }

    [Fact]
    public void Abandon_forgets_without_writing()
    {
        var (s, p, _, _) = Make();
        s.Enable("inf_battery");
        int writes = p.WriteCount;
        s.Abandon();
        Assert.Empty(s.ActiveIds);
        Assert.Equal(writes, p.WriteCount);
    }

    private static System.Text.Json.JsonElement J(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    private static (TrainerSession s, FakeProcess p, List<CheatStateDto> events) FreezeFromGame()
    {
        var p = new FakeProcess(); p.AddModule("g.exe", TestUtil.ModBase, new byte[0x1000]);
        p.AddRegion(0x300000000, new byte[0x1000]);
        var g = new GameDef { Id = "g", Name = "G", ProcessNames = { "g.exe" }, Cheats =
        {
            // g.exe+100 -> attribute object; health current at +0x3C (base +0x38), max health current at +0x5C
            new CheatDef { Id = "inf_hp", Name = "Infinite Health", Type = "toggle", Hint = "Load into your world first",
                Impl = new ImplDef { Type = "pointer", Base = J("\"g.exe+100\""), Offsets = new() { 0x3C }, ValueType = "float", Freeze = true, Restore = false, Mirror = new() { -4 },
                    FreezeFrom = new PointerSourceDef { Offsets = new() { 0x5C } } } },
            new CheatDef { Id = "no_corr", Name = "No Corruption", Type = "toggle",
                Impl = new ImplDef { Type = "pointer", Base = J("\"g.exe+100\""), Offsets = new() { 0x80 }, ValueType = "float", Freeze = true, OnValue = 0 } },
        } };
        var s = new TrainerSession(g, p);
        var events = new List<CheatStateDto>();
        s.Changed += e => events.AddRange(e);
        return (s, p, events);
    }

    [Fact]
    public void FreezeFrom_toggle_waits_for_the_pointer_then_copies_the_source_value()
    {
        var (s, p, events) = FreezeFromGame();
        const ulong obj = 0x300000000;
        s.Enable("inf_hp");                                                                      // chain not readable yet: toggle stays on and waits
        Assert.True(s.IsActive("inf_hp"));
        Assert.Contains(events, e => e.Id == "inf_hp" && e.Enabled == true && e.Hint == "Load into your world first");
        s.Tick();
        Assert.Equal(0, p.WriteCount);                                                           // nothing written while waiting
        p.Write(TestUtil.ModBase + 0x100, BitConverter.GetBytes(obj));                           // player spawned
        p.Write(obj + 0x5C, BitConverter.GetBytes(320f));
        p.Write(obj + 0x3C, BitConverter.GetBytes(12f));
        events.Clear();
        s.Tick();
        Assert.Equal(320f, BitConverter.ToSingle(p.Peek(obj + 0x3C, 4)));                       // current value
        Assert.Equal(320f, BitConverter.ToSingle(p.Peek(obj + 0x38, 4)));                       // mirrored base value
        Assert.Contains(events, e => e.Id == "inf_hp" && e.Enabled == true && e.Hint == null);  // waiting hint cleared
        p.Write(obj + 0x5C, BitConverter.GetBytes(480f));                                        // max health goes up (level/gear)
        p.Write(obj + 0x3C, BitConverter.GetBytes(100f));                                        // took damage
        s.Tick();
        Assert.Equal(480f, BitConverter.ToSingle(p.Peek(obj + 0x3C, 4)));
        p.Write(obj + 0x5C, BitConverter.GetBytes(0f));                                          // source reads 0 (loading): never write it
        p.Write(obj + 0x3C, BitConverter.GetBytes(50f));
        s.Tick();
        Assert.Equal(50f, BitConverter.ToSingle(p.Peek(obj + 0x3C, 4)));
        p.Write(obj + 0x5C, BitConverter.GetBytes(480f));
        s.Tick();
        s.Disable("inf_hp");                                                                     // restore: false -> value stays
        Assert.False(s.IsActive("inf_hp"));
        Assert.Equal(480f, BitConverter.ToSingle(p.Peek(obj + 0x3C, 4)));
        p.Write(obj + 0x3C, BitConverter.GetBytes(10f));
        s.Tick();
        Assert.Equal(10f, BitConverter.ToSingle(p.Peek(obj + 0x3C, 4)));                        // no longer frozen
    }

    [Fact]
    public void Freeze_toggle_with_fixed_value_restores_the_original_and_follows_a_new_object()
    {
        var (s, p, _) = FreezeFromGame();
        const ulong obj = 0x300000000, obj2 = 0x300000400;
        p.Write(TestUtil.ModBase + 0x100, BitConverter.GetBytes(obj));
        p.Write(obj + 0x80, BitConverter.GetBytes(35f));
        s.Enable("no_corr");
        Assert.Equal(0f, BitConverter.ToSingle(p.Peek(obj + 0x80, 4)));
        p.Write(obj + 0x80, BitConverter.GetBytes(20f));
        s.Tick();
        Assert.Equal(0f, BitConverter.ToSingle(p.Peek(obj + 0x80, 4)));
        p.Write(TestUtil.ModBase + 0x100, BitConverter.GetBytes(obj2));                          // respawn / new world: chain is re-resolved every tick
        p.Write(obj2 + 0x80, BitConverter.GetBytes(60f));
        s.Tick();
        Assert.Equal(0f, BitConverter.ToSingle(p.Peek(obj2 + 0x80, 4)));
        s.Disable("no_corr");
        Assert.Equal(35f, BitConverter.ToSingle(p.Peek(obj2 + 0x80, 4)));                       // original value written back
    }

    [Fact]
    public void Aob_pointer_base_is_scanned_once_per_session_for_all_cheats()
    {
        var img = new byte[0x2000];
        // mov rcx,[rip+rel] at 0x500 -> global at 0x1800
        var code = new byte[] { 0xC4, 0xC1, 0x79, 0x2F, 0x4E, 0x18, 0x73, 0x0E, 0x48, 0x8B, 0x0D };
        code.CopyTo(img, 0x500);
        BitConverter.GetBytes(0x1800 - (0x500 + 8 + 7)).CopyTo(img, 0x500 + 11);
        var p = new FakeProcess(); p.AddModule("g.exe", TestUtil.ModBase, img);
        p.AddRegion(0x300000000, new byte[0x1000]);
        p.Write(TestUtil.ModBase + 0x1800, BitConverter.GetBytes(0x300000000UL));
        p.Write(0x300000010, BitConverter.GetBytes(7));
        p.Write(0x300000014, BitConverter.GetBytes(9));
        var b = J("{\"aob\":\"C4 C1 79 2F 4E 18 73 0E 48 8B 0D ?? ?? ?? ??\",\"patternOffset\":8,\"ripOffset\":3,\"insnLength\":7}");
        var bad = J("{\"aob\":\"DE AD BE EF 11 22 33 44\",\"ripOffset\":3,\"insnLength\":7}");
        var g = new GameDef { Id = "g", Name = "G", ProcessNames = { "g.exe" }, Module = "g.exe", Cheats =
        {
            new CheatDef { Id = "a", Name = "A", Type = "number", Impl = new ImplDef { Type = "pointer", Base = b, Offsets = new() { 0x10 }, ValueType = "int32" } },
            new CheatDef { Id = "b", Name = "B", Type = "number", Impl = new ImplDef { Type = "pointer", Base = b, Offsets = new() { 0x14 }, ValueType = "int32" } },
            new CheatDef { Id = "c", Name = "C", Type = "number", Impl = new ImplDef { Type = "pointer", Base = bad, Offsets = new() { 0x14 }, ValueType = "int32" } },
            new CheatDef { Id = "d", Name = "D", Type = "number", Impl = new ImplDef { Type = "pointer", Base = bad, Offsets = new() { 0x10 }, ValueType = "int32" } },
        } };
        var s = new TrainerSession(g, p);
        int scans = 0; s.Runtime.Trace = _ => scans++;
        var values = new Dictionary<string, double?>();
        s.Changed += e => { foreach (var x in e) values[x.Id] = x.Value; };
        s.Tick(); s.Tick(); s.Tick();
        Assert.Equal(7, values["a"]);
        Assert.Equal(9, values["b"]);
        Assert.Null(values["c"]);
        Assert.Equal(2, scans);                                                                  // one scan for the good pattern, one for the bad one
    }
}
