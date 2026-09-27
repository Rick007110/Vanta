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
}
