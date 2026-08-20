namespace PartyApp.Api.Modules.SpyGame;

public enum SpyGamePhase
{
    Idle,       // игра не запущена
    Playing,    // идёт игра
    Finished    // игра завершена
}

public enum RoleType
{
    Spy,
    Townsfolk
}

public class SpyPlayerRole
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public RoleType Role { get; set; }
    public Guid? PartnerId { get; set; }        // для шпиона — напарник
    public string PartnerName { get; set; } = string.Empty;
    public bool IsGuesser { get; set; }
    public string? SecretWord { get; set; }     // для шпиона
    public bool HasAccused { get; set; }        // для горожан — уже делал обвинение
}

public class SpyGameState
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public SpyGamePhase Phase { get; set; } = SpyGamePhase.Idle;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public string SecretWord { get; set; } = string.Empty;
    public List<SpyPlayerRole> Players { get; set; } = new();

    public HashSet<Guid> SpiesWhoSubmittedWord { get; set; } = new();
    
    public Guid? TownWinnerId { get; set; }
    public string? TownWinnerName { get; set; }

    public string? Winner { get; set; }  // "spies", "town", "draw"

    public IEnumerable<SpyPlayerRole> Spies => Players.Where(p => p.Role == RoleType.Spy);
    public IEnumerable<SpyPlayerRole> Townsfolk => Players.Where(p => p.Role == RoleType.Townsfolk);
}

public class SpyGameResult
{
    public string Winner { get; set; } = string.Empty; // "spies", "town", "draw"
    public List<string> SpyNames { get; set; } = new();
    public string? TownWinnerName { get; set; }
    public int PointsAwarded { get; set; }
    public string SecretWord { get; set; } = string.Empty;
}