using Microsoft.AspNetCore.SignalR;

using PartyApp.Api.Hubs;

namespace PartyApp.UnitTests.Testing;

/// <summary>
/// Записывает все рассылки SignalR вместо отправки реальным клиентам.
/// Хранит адресата («all», «user:{id}», …), имя метода и payload.
/// </summary>
public sealed class RecordingHubContext : IHubContext<PartyHub>
{
    private readonly object _lock = new();
    private readonly List<HubCall> _calls = new();

    public RecordingHubContext()
    {
        Clients = new RecordingHubClients(this);
        Groups = new NoopGroupManager();
    }

    public IReadOnlyList<HubCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return _calls.ToList();
            }
        }
    }

    public IHubClients Clients { get; }

    public IGroupManager Groups { get; }

    public IEnumerable<HubCall> CallsFor(string method) => Calls.Where(c => c.Method == method);

    public HubCall SingleCall(string method) => CallsFor(method).Single();

    internal void Record(string target, string method, object?[] args)
    {
        lock (_lock)
        {
            _calls.Add(new HubCall(target, method, args.Length > 0 ? args[0] : null));
        }
    }

    public sealed record HubCall(string Target, string Method, object? Payload);

    private sealed class RecordingHubClients : IHubClients
    {
        private readonly RecordingHubContext _owner;

        public RecordingHubClients(RecordingHubContext owner)
        {
            _owner = owner;
        }

        public IClientProxy All => new RecordingClientProxy(_owner, "all");

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_owner, "all-except");

        public IClientProxy Client(string connectionId) => new RecordingClientProxy(_owner, $"connection:{connectionId}");

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingClientProxy(_owner, "connections");

        public IClientProxy Group(string groupName) => new RecordingClientProxy(_owner, $"group:{groupName}");

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_owner, $"group-except:{groupName}");

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingClientProxy(_owner, "groups");

        public IClientProxy User(string userId) => new RecordingClientProxy(_owner, $"user:{userId}");

        public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingClientProxy(_owner, "users");
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        private readonly RecordingHubContext _owner;
        private readonly string _target;

        public RecordingClientProxy(RecordingHubContext owner, string target)
        {
            _owner = owner;
            _target = target;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _owner.Record(_target, method, args);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}