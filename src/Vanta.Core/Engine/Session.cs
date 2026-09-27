namespace Vanta.Core.Engine;

public sealed class CheatStateDto
{
    public string Id { get; set; } = "";
    public bool? Enabled { get; set; }
    public string? Error { get; set; }
    public double? Value { get; set; }
    public string? Hint { get; set; }
    // serialize nulls for error/hint so the UI clears them
    public Dictionary<string, object?> ToUi()
    {
        var d = new Dictionary<string, object?> { ["id"] = Id, ["error"] = Error, ["hint"] = Hint };
        if (Enabled.HasValue) d["enabled"] = Enabled.Value;
        if (Value.HasValue) d["value"] = Value.Value;
        return d;
    }
}

/// <summary>One attached game process: active cheats, frozen values, value polling. Not thread-safe: use from one thread.</summary>
public sealed class TrainerSession : IDisposable
{
    public GameDef Game { get; }
    public IProcessMemory Memory { get; }
    public CheatRuntime Runtime { get; }
    private readonly Dictionary<string, AppliedCheat> _applied = new();
    private readonly List<string> _order = new();                      // enable order (restore in reverse)
    private readonly Dictionary<string, double> _frozen = new();       // pointer cheats that are frozen
    private readonly Dictionary<string, double?> _toggleOrig = new();  // pointer toggles: original value
    private readonly Dictionary<string, string> _valueState = new();   // last pushed value/hint signature
    private readonly Action<string, string> _log;                       // (level, text)
    public event Action<List<CheatStateDto>>? Changed;

    public TrainerSession(GameDef game, IProcessMemory mem, Action<string, string>? log = null)
    {
        Game = game; Memory = mem; Runtime = new CheatRuntime(mem, game); _log = log ?? ((_, _) => { });
    }

    public CheatDef Cheat(string id) => Game.Cheats.FirstOrDefault(c => c.Id == id) ?? throw new CheatException(Strings.Get("cheat.unknown", id));
    public bool IsActive(string id) => _applied.ContainsKey(id) || _frozen.ContainsKey(id) && Cheat(id).Type == "toggle" || _toggleOrig.ContainsKey(id);
    public IReadOnlyCollection<string> ActiveIds => _order.ToList();

    private static bool IsCode(CheatDef c) => c.Impl.Type is "aobPatch" or "aobInject";

    private void Emit(params CheatStateDto[] s) { if (s.Length > 0) Changed?.Invoke(s.ToList()); }

    /// <summary>Enables a toggle cheat (and, first, the hidden hooks it requires).</summary>
    public void Enable(string id)
    {
        var c = Cheat(id);
        if (IsActive(id)) return;
        EnsureRequires(c);
        if (IsCode(c))
        {
            var a = Runtime.Apply(c);
            _applied[id] = a;
        }
        else if (c.Impl.Type == "pointer")
        {
            var addr = Runtime.ResolveAddress(c) ?? throw new CheatException(c.Hint ?? Strings.Get("ptr.null"));
            _toggleOrig[id] = Runtime.ReadValue(addr, c.Impl.ValueType);
            var v = c.Impl.OnValue ?? 1;
            if (!Runtime.WriteValue(addr, v, c.Impl.ValueType)) throw new CheatException(Strings.Get("write.fail", addr));
            if (c.Impl.Freeze) _frozen[id] = v;
        }
        _order.Remove(id); _order.Add(id);
        if (!c.Hidden) Emit(new CheatStateDto { Id = id, Enabled = true });
        else RefreshDependents(id);
    }

    private void EnsureRequires(CheatDef c)
    {
        foreach (var r in c.Requires ?? new())
        {
            if (IsActive(r)) continue;
            var req = Cheat(r);
            if (!req.Hidden) throw new CheatException(c.Hint ?? Strings.Get("requires.fail", req.Name, Strings.Get("ptr.null")));
            try { Enable(r); }
            catch (CheatException e) { throw new CheatException(Strings.Get("requires.fail", Cheat(r).Name, e.Message)); }
        }
    }

    public void Disable(string id)
    {
        var c = Cheat(id);
        if (_applied.Remove(id, out var a))
        {
            var warn = Runtime.Restore(a);
            if (warn.Count > 0) _log("warn", Strings.Get("restore.warn", c.Name, string.Join("; ", warn)));
        }
        if (_toggleOrig.Remove(id, out var orig))
        {
            _frozen.Remove(id);
            var addr = SafeAddress(c);
            var back = c.Impl.OffValue ?? orig;
            if (addr.HasValue && back.HasValue) Runtime.WriteValue(addr.Value, back.Value, c.Impl.ValueType);
        }
        _order.Remove(id);
        // cheats that depend on this one are no longer usable
        foreach (var d in Game.Cheats.Where(x => x.Requires?.Contains(id) == true && IsActive(x.Id)).ToList()) Disable(d.Id);
        if (!c.Hidden) Emit(new CheatStateDto { Id = id, Enabled = false });
        RefreshDependents(id);
        // hidden helper hooks that were enabled only for this cheat go away with the last user
        foreach (var r in c.Requires ?? new())
        {
            var req = Cheat(r);
            if (req.Hidden && !req.AutoEnable && IsActive(r) && !Game.Cheats.Any(x => x.Requires?.Contains(r) == true && x.Type == "toggle" && IsActive(x.Id))) Disable(r);
        }
    }

    private void RefreshDependents(string hookId)
    {
        foreach (var d in Game.Cheats.Where(x => x.Requires?.Contains(hookId) == true && x.Type != "toggle")) _valueState.Remove(d.Id);
    }

    private ulong? SafeAddress(CheatDef c)
    {
        try { return Runtime.ResolveAddress(c); } catch (CheatException) { return null; }
    }

    public double SetValue(string id, double v)
    {
        var c = Cheat(id);
        if (c.Impl.Type != "pointer") throw new CheatException($"{c.Name}: geen waarde-cheat");
        EnsureRequires(c);
        if (c.Min.HasValue) v = Math.Max(c.Min.Value, v);
        if (c.Max.HasValue) v = Math.Min(c.Max.Value, v);
        var addr = Runtime.ResolveAddress(c) ?? throw new CheatException(c.Hint ?? Strings.Get("ptr.null"));
        if (!Runtime.WriteValue(addr, v, c.Impl.ValueType)) throw new CheatException(Strings.Get("write.fail", addr));
        if (c.Impl.Freeze) _frozen[id] = v;
        _valueState.Remove(id);
        Emit(new CheatStateDto { Id = id, Value = v });
        return v;
    }

    public double Add(string id, double delta)
    {
        var c = Cheat(id);
        var addr = Runtime.ResolveAddress(c) ?? throw new CheatException(c.Hint ?? Strings.Get("ptr.null"));
        var cur = Runtime.ReadValue(addr, c.Impl.ValueType) ?? throw new CheatException(Strings.Get("read.fail", addr));
        return SetValue(id, cur + delta);
    }

    public void Button(string id)
    {
        var c = Cheat(id);
        EnsureRequires(c);
        if (c.Impl.Action == "add") Add(id, c.Impl.Amount ?? 1);
        else SetValue(id, c.Impl.Amount ?? 0);
    }

    /// <summary>Freeze writes + value polling; emits only changes. Call every ~100-500 ms.</summary>
    public void Tick(bool pollValues = true)
    {
        foreach (var (id, v) in _frozen.ToList())
        {
            var addr = SafeAddress(Cheat(id));
            if (addr.HasValue) Runtime.WriteValue(addr.Value, v, Cheat(id).Impl.ValueType);
        }
        if (!pollValues) return;
        var changes = new List<CheatStateDto>();
        foreach (var c in Game.Cheats.Where(x => x.Type is "number" or "slider" && x.Impl.Type == "pointer"))
        {
            CheatStateDto dto;
            bool reqOk = (c.Requires ?? new()).All(IsActive);
            ulong? addr = reqOk ? SafeAddress(c) : null;
            double? val = addr.HasValue ? Runtime.ReadValue(addr.Value, c.Impl.ValueType) : null;
            if (val.HasValue && double.IsFinite(val.Value)) dto = new CheatStateDto { Id = c.Id, Value = Math.Round(val.Value, 3), Hint = null, Error = null };
            else dto = new CheatStateDto { Id = c.Id, Hint = c.Hint ?? Strings.Get("ptr.null"), Error = null };
            var sig = $"{dto.Value}|{dto.Hint}";
            if (_valueState.TryGetValue(c.Id, out var old) && old == sig) continue;
            _valueState[c.Id] = sig;
            changes.Add(dto);
        }
        if (changes.Count > 0) Changed?.Invoke(changes);
    }

    /// <summary>Disables everything (reverse order) and writes reset values. Safe to call more than once.</summary>
    public void RestoreAll(bool resetValues = false)
    {
        foreach (var id in _order.AsEnumerable().Reverse().ToList())
        {
            try { Disable(id); } catch (Exception e) { _log("warn", $"{id}: {e.Message}"); }
        }
        _frozen.Clear();
        if (resetValues)
            foreach (var c in Game.Cheats.Where(x => x.ResetValue.HasValue && x.Impl.Type == "pointer"))
            {
                var addr = SafeAddress(c);
                if (addr.HasValue) Runtime.WriteValue(addr.Value, c.ResetValue!.Value, c.Impl.ValueType);
            }
    }

    /// <summary>Forget the last pushed value signatures so the next Tick re-emits every value.</summary>
    public void ResetValueCache() => _valueState.Clear();

    /// <summary>The process is gone: forget everything without touching memory.</summary>
    public void Abandon()
    {
        _applied.Clear(); _order.Clear(); _frozen.Clear(); _toggleOrig.Clear(); _valueState.Clear();
    }

    public void Dispose() => Memory.Dispose();
}
