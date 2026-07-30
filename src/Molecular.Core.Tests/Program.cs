using Molecular.Core.Models;
using Molecular.Core.Audio;
using Molecular.Core.Persistence;
using Molecular.Core.Safety;
using Molecular.Core.Runtime;

var tests = new (string Name, Action Run)[]
{
    ("migra perfil fixo para canais dinamicos", MigratesFixedProfile),
    ("impede atribuicao duplicada", RejectsDuplicateAssignment),
    ("persiste estado oculto", PersistsHiddenState),
    ("respeita teto de seguranca", RespectsSafetyCeiling),
    ("monitora sessoes do Windows sem reenummerar", ReadsCachedWindowsSessions),
    ("remove e restaura canal sem perder estado", RemovesAndRestoresChannel),
    ("impede uma segunda instancia", RejectsSecondInstance),
    ("remove teto individual oculto", RemovesLegacyHiddenCeiling)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
    }
}

foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

static void MigratesFixedProfile()
{
    using var temporary = new TemporaryProfile();
    File.WriteAllText(temporary.Path, """
    {
      "SchemaVersion": 6,
      "Name": "Principal",
      "Channels": [
        { "Index": 1, "Order": 1, "ApplicationKey": "edge", "ApplicationName": "Edge", "ExecutableName": "msedge.exe", "TargetVolume": 22 },
        { "Index": 2, "Order": 2, "ApplicationKey": null },
        { "Index": 8, "Order": 8, "ApplicationKey": "spotify", "ApplicationName": "Spotify", "ExecutableName": "Spotify.exe", "TargetVolume": 18 }
      ]
    }
    """);

    var profile = new ProfileStore(temporary.Path).Load();
    Equal(8, profile.SchemaVersion, "schema");
    Equal(2, profile.Channels.Count, "quantidade de canais");
    Equal("edge", profile.Channels[0].ApplicationKey, "primeira atribuicao");
    Equal(1, profile.Channels[0].Index, "primeiro indice");
    Equal(2, profile.Channels[1].Index, "segundo indice");
}

static void RejectsDuplicateAssignment()
{
    var profile = new MixerProfile();
    MixerChannelRegistry.Add(profile, "edge", "Edge", "msedge.exe", null, 20);
    var rejected = false;
    try
    {
        MixerChannelRegistry.Add(profile, "EDGE", "Edge", "msedge.exe", null, 20);
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }

    True(rejected, "uma chave ja atribuida deve ser rejeitada sem diferenciar maiusculas");
    Equal(1, profile.Channels.Count, "a lista nao pode ser alterada na falha");
}

static void PersistsHiddenState()
{
    using var temporary = new TemporaryProfile();
    var store = new ProfileStore(temporary.Path);
    var profile = new MixerProfile();
    var channel = MixerChannelRegistry.Add(profile, "spotify", "Spotify", "Spotify.exe", null, 12);
    channel.IsHidden = true;
    store.Save(profile);

    var restored = store.Load();
    True(restored.Channels.Single().IsHidden, "canal oculto deve continuar oculto");
}

static void RespectsSafetyCeiling()
{
    var engine = new SafetyEngine(new SafetyPolicy
    {
        Enabled = true,
        GlobalCeiling = 35,
        NewSessionVolume = 50
    });
    Equal(35d, engine.SafeInitialVolume(), "volume inicial seguro");
    Equal(20d, engine.Clamp(80, 20), "teto individual");
}

static void RemovesAndRestoresChannel()
{
    var profile = new MixerProfile();
    var channel = MixerChannelRegistry.Add(profile, "spotify", "Spotify", "Spotify.exe", null, 18);
    channel.IsMuted = true;
    channel.IsHidden = true;

    True(MixerChannelRegistry.Remove(profile, channel), "canal deve ser removido");
    Equal(0, profile.Channels.Count, "perfil deve ficar sem o canal");
    True(MixerChannelRegistry.Restore(profile, channel), "canal deve ser restaurado");
    True(ReferenceEquals(channel, profile.Channels.Single()), "a mesma configuracao deve ser preservada");
    True(profile.Channels.Single().IsMuted, "mute deve ser preservado");
    True(profile.Channels.Single().IsHidden, "estado oculto deve ser preservado");
}

static void RejectsSecondInstance()
{
    var id = $"Molecular.Tests.{Guid.NewGuid():N}";
    True(SingleInstanceCoordinator.TryAcquire(id, out var first), "primeira instancia deve adquirir o bloqueio");
    using (first)
    {
        True(!SingleInstanceCoordinator.TryAcquire(id, out var second), "segunda instancia deve ser rejeitada");
        True(second is null, "segunda instancia nao deve manter recursos");
    }
}

static void RemovesLegacyHiddenCeiling()
{
    using var temporary = new TemporaryProfile();
    File.WriteAllText(temporary.Path, """
    {
      "SchemaVersion": 7,
      "Name": "Principal",
      "Safety": { "Enabled": true, "GlobalCeiling": 100 },
      "Channels": [
        { "Index": 1, "Order": 1, "ApplicationKey": "game", "ApplicationName": "Game", "Ceiling": 50, "TargetVolume": 50 }
      ]
    }
    """);

    var profile = new ProfileStore(temporary.Path).Load();
    Equal(8, profile.SchemaVersion, "schema migrado");
    Equal(100d, profile.Channels.Single().Ceiling, "teto oculto deve ser removido");
}

static void ReadsCachedWindowsSessions()
{
    if (!OperatingSystem.IsWindows()) return;

    using var audio = new WindowsAudioSessionService();
    var first = Task.Run(audio.ReadApplications).GetAwaiter().GetResult();
    Thread.Sleep(120);
    var second = Task.Run(audio.ReadApplications).GetAwaiter().GetResult();
    var devices = audio.ReadOutputDevices();
    True(devices.Count > 0, "ao menos um dispositivo de saida deve estar disponivel");
    True(first.All(item => !string.IsNullOrWhiteSpace(item.Key)), "as sessoes iniciais devem ter identidade");
    True(second.All(item => !string.IsNullOrWhiteSpace(item.Key)), "as sessoes em cache devem continuar validas");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: esperado {expected}, recebido {actual}");
}

sealed class TemporaryProfile : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"molecular-tests-{Guid.NewGuid():N}");

    public TemporaryProfile()
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "profile.json");
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
