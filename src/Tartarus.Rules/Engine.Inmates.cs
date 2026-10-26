namespace Tartarus.Rules;

public sealed partial class Engine
{
    /// <summary>
    /// Who opened a door. A move's door (Patrol) and an instrument's or incident's whose card doesn't say "does not
    /// trigger other effects" (Effect) set off the "after opening" instruments; the run's start and side doors don't.
    /// </summary>
    internal enum How : byte { Patrol, Effect, Side, Start }

    // ---------- Inmates ----------

    private static Inmate NewInmate(ref GameState s, Code code, Rarity rarity)
    {
        if (rarity > s.TopRarity) rarity = s.TopRarity;
        return new Inmate { Id = s.NextId++, Code = code, Rarity = rarity };
    }

    private static Code RandomCode(ref GameState s) => (Code)s.Rng.Below(4);

    /// <summary>Rarity weights for whoever is behind a door: the Sorting Hook keeps out Normal.</summary>
    private void DoorWeights(in GameState s, Span<double> w)
    {
        Options.DoorWeights.AsSpan(0, 5).CopyTo(w);
        if (s.Has(Instrument.Hook)) w[0] = 0;
    }

    private Inmate RollDoorInmate(ref GameState s)
    {
        Span<double> w = stackalloc double[5];
        DoorWeights(s, w);
        return NewInmate(ref s, RandomCode(ref s), (Rarity)s.Rng.Pick(w));
    }

    /// <summary>Who is behind the Unopened cell <paramref name="n"/>, drawn the first time anyone looks.</summary>
    private Inmate HiddenOf(ref GameState s, int n)
    {
        ref var c = ref s.Cells[n];
        if (c.Inmate.IsNone) c.Inmate = RollDoorInmate(ref s);
        return c.Inmate;
    }

    /// <summary>
    /// Opens the Unopened cell <paramref name="n"/>: the Inmate behind it comes out (or <paramref name="forced"/>,
    /// as an incident or instrument places one). A Normal nobody has seen yet stays out under the Sorting Hook.
    /// </summary>
    private void OpenCell(ref GameState s, int n, How how, Inmate forced = default)
    {
        ref var c = ref s.Cells[n];
        if (!c.Sealed || n == GameData.Room) return;
        Inmate inmate;
        if (!forced.IsNone) inmate = forced;
        else if (!c.Inmate.IsNone && !(c.Inmate.Rarity == Rarity.Normal && !c.Has(CellMarks.RarityShown) && s.Has(Instrument.Hook))) inmate = c.Inmate;
        else inmate = RollDoorInmate(ref s);
        c = ref s.Cells[n];
        c.Sealed = false;
        c.Inmate = inmate;
        c.Marks = CellMarks.None;
        c.NestDue = 0;
        if (how != How.Start)
        {
            s.Stats.Opened++;
            AdvanceObjective(ref s, ObjectiveKind.BehindCellDoors, 1);
        }
        AfterGrowth(ref s, n);
        if ((how == How.Patrol || how == How.Effect) && s.Cells[n].HasInmate && s.Cells[n].Inmate.Id == inmate.Id) AfterOpening(ref s, n, inmate.Id);
    }

    /// <summary>The cell's Inmate leaves (consumed, removed); the cell is Unopened again, its marks gone.</summary>
    private static void EmptyCell(ref GameState s, int n)
    {
        ref var c = ref s.Cells[n];
        c.Sealed = true;
        c.Inmate = default;
        c.Marks = CellMarks.None;
        c.NestDue = 0;
    }

    /// <summary>
    /// The Inmate <paramref name="id"/> rises <paramref name="by"/> tiers (at most to <see cref="GameState.TopRarity"/>);
    /// a Consume also gives it a random Code. Returns its cell, or -1 if it left the Sandbox (Chromatic).
    /// </summary>
    private int Promote(ref GameState s, ushort id, int by, bool reroll)
    {
        if (FindCell(s, id) is not int n || !s.Cells[n].HasInmate) return -1;
        ref var c = ref s.Cells[n];
        int r = Math.Clamp((int)c.Inmate.Rarity + by, 0, (int)s.TopRarity);
        c.Inmate.Rarity = (Rarity)r;
        if (reroll && c.Inmate.Rarity < Rarity.Chromatic) c.Inmate.Code = RandomCode(ref s);
        AfterGrowth(ref s, n);
        return s.Cells[n].HasInmate ? n : -1;
    }

    /// <summary>
    /// What a rarity brings: King of the Caged's progress at Ultimate; at Chromatic the Inmate goes to the Chromatic
    /// panel, its cell is Unopened again, and a Warden's Treasure is picked.
    /// </summary>
    private void AfterGrowth(ref GameState s, int n)
    {
        ref var c = ref s.Cells[n];
        if (!c.HasInmate) return;
        if (c.Inmate.Rarity >= Rarity.Ultimate) AdvanceObjective(ref s, ObjectiveKind.KingOfTheCaged, 1);
        if (s.Cells[n].Inmate.Rarity < Rarity.Chromatic) return;
        EmptyCell(ref s, n);
        s.Chromatic++;
        CheckPrey(ref s);
        PushPending(ref s, new Pending { Kind = PendingKind.Treasure, A = 0b1111, Picks = (byte)(s.Theme == Theme.Opulence ? 3 : 1) });
    }

    private void AdvanceObjective(ref GameState s, ObjectiveKind kind, int amount)
    {
        if (s.ObjectiveDone || s.Objective != kind || amount <= 0) return;
        int target = GameData.Target(kind);
        s.ObjectiveProgress = (byte)Math.Min(target, s.ObjectiveProgress + amount);
        if (s.ObjectiveProgress < target) return;
        s.ObjectiveDone = true;
        var offer = DrawOffer(ref s, GameData.InstrumentOffer);
        offer.ForObjective = true;
        if (offer.Count > 0) PushPending(ref s, offer);
    }

    // ---------- Choices waiting ----------

    private static void PushPending(ref GameState s, Pending p, bool front = false)
    {
        if (s.PendingCount >= PendingArray.MaxPending) return;
        if (front)
        {
            for (int i = s.PendingCount; i > 0; i--) s.Pending[i] = s.Pending[i - 1];
            s.Pending[0] = p;
        }
        else s.Pending[s.PendingCount] = p;
        s.PendingCount++;
    }

    private static void PopPending(ref GameState s) => RemovePending(ref s, 0);

    private static void RemovePending(ref GameState s, int at)
    {
        for (int i = at; i < s.PendingCount - 1; i++) s.Pending[i] = s.Pending[i + 1];
        s.PendingCount--;
        s.Pending[s.PendingCount] = default;
    }

    // ---------- The Sandbox ----------

    public static int? FindCell(in GameState s, ushort id)
    {
        if (id == 0) return null;
        for (int n = 1; n < GameData.Cells; n++)
            if (!s.Cells[n].Sealed && s.Cells[n].Inmate.Id == id) return n;
        return null;
    }

    /// <summary>The ids of the Inmates on the Sandbox (opened cells), in cell order.</summary>
    public static int BoardIds(in GameState s, Span<ushort> into)
    {
        int k = 0;
        for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].HasInmate) into[k++] = s.Cells[n].Inmate.Id;
        return k;
    }

    public static int BoardCount(in GameState s)
    {
        int k = 0;
        for (int n = 1; n < GameData.Cells; n++) if (s.Cells[n].HasInmate) k++;
        return k;
    }

    /// <summary>The Unopened cells (a Rat Nest's too unless <paramref name="noNests"/>).</summary>
    public static int SealedCells(in GameState s, Span<byte> into, bool noNests = false)
    {
        int k = 0;
        for (int n = 1; n < GameData.Cells; n++)
            if (s.Cells[n].Sealed && !(noNests && s.Cells[n].NestDue != 0)) into[k++] = (byte)n;
        return k;
    }

    /// <summary>Opens up to <paramref name="count"/> random Unopened cells.</summary>
    private void OpenRandom(ref GameState s, int count, How how)
    {
        Span<byte> cells = stackalloc byte[GameData.Cells];
        int k = SealedCells(s, cells);
        s.Rng.Shuffle(cells[..k]);
        for (int i = 0; i < Math.Min(count, k); i++) OpenCell(ref s, cells[i], how);
    }

    private static int RandomSealed(ref GameState s)
    {
        Span<byte> cells = stackalloc byte[GameData.Cells];
        int k = SealedCells(s, cells);
        return k == 0 ? -1 : cells[s.Rng.Below(k)];
    }

    /// <summary>The cell <paramref name="step"/> places from <paramref name="n"/> on the ring.</summary>
    private static int Ring(int n, int step) => ((n + step) % GameData.Cells + GameData.Cells) % GameData.Cells;

    // ---------- Prey marks and Rat Nests ----------

    /// <summary>A Prey mark ends when its Lion or its prey leaves the Sandbox or changes Code.</summary>
    private static void CheckPrey(ref GameState s)
    {
        for (int i = s.PreyCount - 1; i >= 0; i--)
        {
            var l = s.Prey[i];
            bool ok = FindCell(s, l.Lion) is int a && s.Cells[a].Inmate.Code == Code.Lion
                      && FindCell(s, l.Prey) is int b && s.Cells[b].Inmate.Code == l.PreyCode;
            if (ok) continue;
            for (int j = i; j < s.PreyCount - 1; j++) s.Prey[j] = s.Prey[j + 1];
            s.PreyCount--;
        }
    }

    /// <summary>Each morning a Prey mark raises its Lion (once, however many marks it has) and its prey a tier.</summary>
    private void PreyRise(ref GameState s)
    {
        Span<ushort> risen = stackalloc ushort[PreyArray.MaxPrey];
        int r = 0;
        for (int i = 0; i < s.PreyCount; i++)
        {
            var l = s.Prey[i];
            if (l.NextDay > s.Day) continue;
            s.Prey[i].Rounds--;
            s.Prey[i].NextDay = (byte)(s.Day + 1);
            bool lionRises = risen[..r].IndexOf(l.Lion) < 0;
            if (lionRises) risen[r++] = l.Lion;
            if (lionRises) Promote(ref s, l.Lion, 1, reroll: false);
            Promote(ref s, l.Prey, 1, reroll: false);
        }
        for (int i = s.PreyCount - 1; i >= 0; i--)
        {
            if (s.Prey[i].Rounds > 0) continue;
            for (int j = i; j < s.PreyCount - 1; j++) s.Prey[j] = s.Prey[j + 1];
            s.PreyCount--;
        }
    }

    /// <summary>A Rat Nest that is due opens its cell with a Rare Colossal Rat.</summary>
    private void HatchNests(ref GameState s)
    {
        for (int n = 1; n < GameData.Cells; n++)
        {
            if (s.Cells[n].NestDue == 0 || s.Cells[n].NestDue > s.Day) continue;
            if (!s.Cells[n].Sealed)
            {
                s.Cells[n].NestDue = 0;
                continue;
            }
            OpenCell(ref s, n, How.Side, NewInmate(ref s, Code.Rat, Options.NestRarity));
        }
    }
}
