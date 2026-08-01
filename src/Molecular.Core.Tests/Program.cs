using Molecular.Core.Models;
using Molecular.Core.Audio;
using Molecular.Core.Diagnostics;
using Molecular.Core.Persistence;
using Molecular.Core.Safety;
using Molecular.Core.Runtime;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("migra perfil fixo para canais dinamicos", MigratesFixedProfile),
    ("impede atribuicao duplicada", RejectsDuplicateAssignment),
    ("persiste estado oculto", PersistsHiddenState),
    ("respeita teto de seguranca", RespectsSafetyCeiling),
    ("monitora sessoes do Windows sem reenummerar", ReadsCachedWindowsSessions),
    ("remove e restaura canal sem perder estado", RemovesAndRestoresChannel),
    ("impede uma segunda instancia", RejectsSecondInstance),
    ("remove teto individual oculto", RemovesLegacyHiddenCeiling),
    ("cria backup ao salvar perfil", CreatesBackupOnSave),
    ("recupera perfil corrompido pelo backup", RecoversCorruptProfileFromBackup),
    ("log operacional rotaciona por tamanho", RotatesOperationalLogBySize),
    ("ciclo completo adicionar ocultar remover restaurar", CompletesChannelLifecycle),
    ("reconstrói monitor apos pedido de rebuild", RebuildsSessionMonitorOnRequest),
    ("migra perfil unico para catalogo", MigratesLegacyProfileToCatalog),
    ("ativa perfil atrelado e volta ao padrao", SwitchesBoundProfileAndRestoresDefault),
    ("exporta perfil sem caminho local", ExportsProfileWithoutLocalPaths),
    ("importa perfil com novo id", ImportsProfileWithFreshId),
    ("reordena e fixa canais", ReordersAndPinsChannels),
    ("persiste busca automatica por perfil", PersistsAutoDiscoverFlag),
    ("permite catalogo vazio sem perfil", AllowsEmptyProfileCatalog),
    ("persiste configuracoes do aplicativo", PersistsAppSettings),
    ("preserva dispositivo preferido durante fallback", PreservesPreferredOutputDuringFallback),
    ("respeita fallback de dispositivo desativado", RespectsDisabledOutputFallback),
    ("rearma perfil atrelado apos escolha manual", RearmsBoundProfileAfterManualChoice),
    ("persiste bloqueio de canal removido no auto discover", PersistsAutoDiscoverSuppression)
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
    Equal(11, profile.SchemaVersion, "schema");
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
    Equal(11, profile.SchemaVersion, "schema migrado");
    Equal(100d, profile.Channels.Single().Ceiling, "teto oculto deve ser removido");
}

static void CreatesBackupOnSave()
{
    using var temporary = new TemporaryProfile();
    var store = new ProfileStore(temporary.Path);
    var first = new MixerProfile { Name = "Primeiro" };
    MixerChannelRegistry.Add(first, "edge", "Edge", "msedge.exe", null, 20);
    store.Save(first);

    var second = new MixerProfile { Name = "Segundo" };
    MixerChannelRegistry.Add(second, "spotify", "Spotify", "Spotify.exe", null, 15);
    store.Save(second);

    True(File.Exists(store.BackupPath), "backup deve existir apos a segunda gravacao");
    var backupCatalog = JsonSerializer.Deserialize<ProfileCatalog>(File.ReadAllText(store.BackupPath));
    Equal("Primeiro", backupCatalog!.Profiles.Single().Name, "backup deve preservar a versao anterior");
    Equal("edge", backupCatalog.Profiles.Single().Channels.Single().ApplicationKey, "backup deve preservar canais anteriores");
}

static void RecoversCorruptProfileFromBackup()
{
    using var temporary = new TemporaryProfile();
    var store = new ProfileStore(temporary.Path);
    var healthy = new MixerProfile { Name = "Saudavel" };
    MixerChannelRegistry.Add(healthy, "spotify", "Spotify", "Spotify.exe", null, 18);
    store.Save(healthy);
    store.Save(new MixerProfile { Name = "Atual" });

    File.WriteAllText(temporary.Path, "{ isto nao e json valido");
    var result = store.LoadDetailed();

    True(result.RecoveredFromBackup, "deve recuperar pelo backup");
    True(!result.ResetToDefault, "nao deve resetar quando o backup e valido");
    Equal("Saudavel", result.Profile.Name, "nome restaurado");
    Equal("spotify", result.Profile.Channels.Single().ApplicationKey, "canal restaurado");
    True(!string.IsNullOrWhiteSpace(result.Notice), "usuario deve ser avisado");
    True(Directory.GetFiles(temporary.RootDirectory, "profile.json.corrupt-*").Length > 0, "arquivo corrompido deve ser isolado");
}

static void RotatesOperationalLogBySize()
{
    var directory = Path.Combine(Path.GetTempPath(), $"molecular-log-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var log = new OperationalLog(directory);
        var chunk = new string('x', 350);
        for (var index = 0; index < 900; index++)
            log.Info("test", chunk);

        True(File.Exists(log.ActiveFilePath), "log ativo deve existir");
        True(File.Exists(Path.Combine(directory, "operational.1.log")), "rotacao deve criar operational.1.log");
        var recent = log.ReadRecentLines(50);
        True(recent.Count > 0, "deve ler linhas recentes");
        True(recent.All(line => !line.Contains("artista", StringComparison.OrdinalIgnoreCase)), "nao deve inventar metadados de midia");

        var report = DiagnosticsExporter.BuildReport(new DiagnosticsSnapshot(
            "0.2.2",
            "0.2.2.0",
            ".NET",
            "Windows",
            "ABCD",
            125,
            "Speakers",
            3,
            2,
            1,
            "Sistema de áudio ativo",
            false,
            recent));
        True(report.Contains("Speakers", StringComparison.Ordinal), "relatorio deve incluir dispositivo");
        True(report.Contains("sem títulos", StringComparison.OrdinalIgnoreCase), "relatorio deve declarar ausencia de midia");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void CompletesChannelLifecycle()
{
    using var temporary = new TemporaryProfile();
    var store = new ProfileStore(temporary.Path);
    var profile = new MixerProfile();
    var channel = MixerChannelRegistry.Add(profile, "spotify", "Spotify", "Spotify.exe", null, 22);
    channel.IsMuted = true;
    channel.IsSolo = true;
    channel.ViewMode = "expanded";
    channel.IsHidden = true;
    store.Save(profile);

    var hidden = store.Load();
    True(hidden.Channels.Single().IsHidden, "ocultar deve persistir");
    hidden.Channels.Single().IsHidden = false;
    store.Save(hidden);

    var visible = store.Load();
    True(!visible.Channels.Single().IsHidden, "restaurar visibilidade deve persistir");
    var binding = visible.Channels.Single();
    True(MixerChannelRegistry.Remove(visible, binding), "remover deve funcionar");
    Equal(0, visible.Channels.Count, "perfil sem canal apos remocao");
    store.Save(visible);

    var empty = store.Load();
    Equal(0, empty.Channels.Count, "remocao deve sobreviver ao reload");
    True(MixerChannelRegistry.Restore(empty, binding), "desfazer/restaurar deve recolocar o canal");
    True(empty.Channels.Single().IsMuted, "mute preservado no desfazer");
    True(empty.Channels.Single().IsSolo, "solo preservado no desfazer");
    Equal("expanded", empty.Channels.Single().ViewMode, "view mode preservado no desfazer");
}

static void RebuildsSessionMonitorOnRequest()
{
    if (!OperatingSystem.IsWindows()) return;

    using var audio = new WindowsAudioSessionService();
    var before = Task.Run(audio.ReadApplications).GetAwaiter().GetResult();
    var devicesBefore = audio.ReadOutputDevices();
    True(devicesBefore.Count > 0, "deve haver dispositivo antes do rebuild");

    audio.RequestSessionRebuild();
    var after = Task.Run(audio.ReadApplications).GetAwaiter().GetResult();
    var devicesAfter = audio.ReadOutputDevices();
    True(devicesAfter.Count > 0, "dispositivo deve continuar disponivel apos rebuild");
    True(after.All(item => !string.IsNullOrWhiteSpace(item.Key)), "sessoes apos rebuild devem ter identidade");
    _ = before;
}

static void MigratesLegacyProfileToCatalog()
{
    using var temporary = new TemporaryProfile();
    File.WriteAllText(temporary.Path, """
    {
      "SchemaVersion": 8,
      "Name": "Principal",
      "Channels": [
        { "Index": 1, "Order": 1, "ApplicationKey": "discord", "ApplicationName": "Discord", "ExecutableName": "Discord.exe", "TargetVolume": 40 }
      ]
    }
    """);

    var result = new ProfileStore(temporary.Path).LoadDetailed();
    Equal(1, result.Catalog.Profiles.Count, "um perfil migrado");
    Equal("Principal", result.Catalog.ActiveProfile.Name, "nome preservado");
    True(!string.IsNullOrWhiteSpace(result.Catalog.ActiveProfile.Id), "id gerado");
    Equal(result.Catalog.ActiveProfileId, result.Catalog.DefaultProfileId, "ativo e padrao iguais apos migracao");
    Equal("discord", result.Catalog.ActiveProfile.Channels.Single().ApplicationKey, "canal preservado");
}

static void SwitchesBoundProfileAndRestoresDefault()
{
    var catalog = ProfileCatalog.CreateDefault();
    var call = new MixerProfile
    {
        Name = "Call Discord",
        BoundApplicationKey = "process:discord",
        BoundApplicationName = "Discord"
    };
    MixerChannelRegistry.Add(call, "process:discord", "Discord", "Discord.exe", null, 70);
    MixerChannelRegistry.Add(call, "process:msedge", "Microsoft Edge", "msedge.exe", null, 25);
    catalog.Profiles.Add(call);

    var matched = catalog.FindBoundToApplication("process:discord");
    True(matched is not null, "deve achar perfil atrelado");
    Equal("Call Discord", matched!.Name, "perfil de call");
    Equal(70d, matched.Channels.First(channel => channel.ApplicationKey == "process:discord").TargetVolume, "volume discord");
    Equal(25d, matched.Channels.First(channel => channel.ApplicationKey == "process:msedge").TargetVolume, "volume edge");

    catalog.ActiveProfileId = matched.Id;
    Equal("Call Discord", catalog.ActiveProfile.Name, "ativo apos bind");
    catalog.ActiveProfileId = catalog.DefaultProfileId;
    Equal("Principal", catalog.ActiveProfile.Name, "volta ao padrao");
}

static void ExportsProfileWithoutLocalPaths()
{
    var profile = new MixerProfile { Name = "Discord", AutoDiscoverChannels = true };
    var channel = MixerChannelRegistry.Add(profile, "process:discord", "Discord", "Discord.exe", @"C:\Users\test\AppData\Discord.exe", 40);
    channel.IsPinned = true;
    var exported = ProfileTransfer.SanitizeForExport(profile);
    True(exported.AutoDiscoverChannels, "flag de busca automatica exportada");
    True(exported.Channels.Single().ExecutablePath is null, "caminho local nao deve ser exportado");
    Equal("Discord.exe", exported.Channels.Single().ExecutableName, "nome do executavel permanece");
}

static void ImportsProfileWithFreshId()
{
    var original = new MixerProfile { Name = "Edge", Id = "abc123" };
    MixerChannelRegistry.Add(original, "process:msedge", "Edge", "msedge.exe", @"D:\Apps\msedge.exe", 30);
    var json = ProfileTransfer.ExportJson(original);
    var imported = ProfileTransfer.ImportJson(json);
    True(!string.Equals(imported.Id, original.Id, StringComparison.OrdinalIgnoreCase), "import deve gerar novo id");
    Equal("Edge", imported.Name, "nome preservado");
    True(imported.Channels.Single().ExecutablePath is null, "path removido no import");
    Equal(11, imported.SchemaVersion, "schema atual");
}

static void ReordersAndPinsChannels()
{
    var profile = new MixerProfile();
    var edge = MixerChannelRegistry.Add(profile, "edge", "Edge", "msedge.exe", null, 20);
    var discord = MixerChannelRegistry.Add(profile, "discord", "Discord", "Discord.exe", null, 40);
    var spotify = MixerChannelRegistry.Add(profile, "spotify", "Spotify", "Spotify.exe", null, 30);
    True(MixerChannelRegistry.Move(profile, discord, -1), "mover discord para esquerda");
    Equal("discord", profile.Channels.OrderBy(c => c.Order).First().ApplicationKey, "discord virou primeiro");

    MixerChannelRegistry.SetPinned(profile, spotify, true);
    Equal("spotify", MixerChannelRegistry.Sorted(profile).First().ApplicationKey, "fixado fica na frente");
    True(!MixerChannelRegistry.Move(profile, spotify, 1), "fixado nao troca com nao-fixado");
    Equal("#25D7E8", MixerChannelRegistry.CycleAccent(edge), "ciclo de cor");
}

static void PersistsAutoDiscoverFlag()
{
    using var temporary = new TemporaryProfile();
    var store = new ProfileStore(temporary.Path);
    var catalog = ProfileCatalog.CreateDefault();
    catalog.ActiveProfile.Name = "Discord";
    catalog.ActiveProfile.AutoDiscoverChannels = true;
    store.SaveCatalog(catalog);

    var restored = store.LoadDetailed().Catalog.ActiveProfile;
    True(restored.AutoDiscoverChannels, "busca automatica deve persistir");
    Equal(11, restored.SchemaVersion, "schema 11");
}

static void AllowsEmptyProfileCatalog()
{
    using var temporary = new TemporaryProfile();
    var store = new ProfileStore(temporary.Path);
    store.SaveCatalog(new ProfileCatalog
    {
        CatalogVersion = 1,
        ActiveProfileId = string.Empty,
        DefaultProfileId = string.Empty,
        Profiles = []
    });

    var loaded = store.LoadDetailed().Catalog;
    Equal(0, loaded.Profiles.Count, "catalogo pode ficar vazio");
    True(string.IsNullOrEmpty(loaded.ActiveProfileId), "sem perfil ativo");
    True(loaded.ActiveProfileOrNull is null, "nenhum perfil resolvido");
}

static void PersistsAppSettings()
{
    using var temporary = new TemporaryProfile();
    var path = System.IO.Path.Combine(temporary.RootDirectory, "settings.json");
    var store = new AppSettingsStore(path);
    store.Save(new AppSettings
    {
        CloseToTray = false,
        StartInTray = true,
        PreferredOutputDeviceId = "device- Speakers",
        PreferSystemDefaultFallback = true,
        MeterIntervalMs = 200
    });

    var loaded = store.Load();
    True(!loaded.CloseToTray, "fechar para bandeja");
    True(loaded.StartInTray, "abrir na bandeja");
    Equal("device- Speakers", loaded.PreferredOutputDeviceId, "dispositivo preferido");
    Equal(200, loaded.MeterIntervalMs, "intervalo dos medidores");

    var normalized = AppSettingsStore.Normalize(new AppSettings { MeterIntervalMs = 999 });
    Equal(500, normalized.MeterIntervalMs, "intervalo fora da faixa normaliza para 500");
}

static void PreservesPreferredOutputDuringFallback()
{
    var devices = new[]
    {
        new AudioOutputDevice("default", "Alto-falantes", IsDefault: true),
        new AudioOutputDevice("hdmi", "Monitor", IsDefault: false)
    };

    var selection = OutputDeviceSelectionResolver.Resolve(devices, "usb-headset", allowDefaultFallback: true);
    Equal("default", selection.DisplayDevice!.Id, "fallback visual deve usar o padrao");
    Equal("usb-headset", selection.PreferredDeviceId, "fallback nao pode apagar a preferencia");
    True(selection.IsPreferredUnavailable, "preferido deve ser marcado indisponivel");
    True(selection.IsUsingFallback, "fallback deve ser sinalizado");
}

static void RespectsDisabledOutputFallback()
{
    var devices = new[]
    {
        new AudioOutputDevice("default", "Alto-falantes", IsDefault: true)
    };

    var selection = OutputDeviceSelectionResolver.Resolve(devices, "usb-headset", allowDefaultFallback: false);
    True(selection.DisplayDevice is null, "nao deve selecionar outro dispositivo com fallback desligado");
    Equal("usb-headset", selection.PreferredDeviceId, "preferencia indisponivel deve ser preservada");
    True(selection.IsPreferredUnavailable, "preferido deve ser marcado indisponivel");
    True(!selection.IsUsingFallback, "fallback deve permanecer desligado");
}

static void RearmsBoundProfileAfterManualChoice()
{
    var gate = new BoundProfileActivationGate();
    True(gate.CanActivateMatch(), "perfil atrelado deve iniciar armado");
    gate.SuppressCurrentMatches();
    True(!gate.CanActivateMatch(), "escolha manual deve vencer o app atualmente aberto");
    gate.ObserveNoMatch();
    True(gate.CanActivateMatch(), "fechar o app deve rearmar a proxima ativacao");
}

static void PersistsAutoDiscoverSuppression()
{
    using var temporary = new TemporaryProfile();
    var store = new ProfileStore(temporary.Path);
    var profile = new MixerProfile { AutoDiscoverChannels = true };
    MixerChannelRegistry.SuppressAutoDiscover(profile, "process:spotify");
    store.Save(profile);

    var restored = store.Load();
    True(
        MixerChannelRegistry.IsAutoDiscoverSuppressed(restored, "PROCESS:SPOTIFY"),
        "canal removido deve continuar bloqueado apos reiniciar");
    MixerChannelRegistry.AllowAutoDiscover(restored, "process:spotify");
    True(
        !MixerChannelRegistry.IsAutoDiscoverSuppressed(restored, "process:spotify"),
        "adicao manual deve liberar o aplicativo novamente");
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
        System.IO.Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "profile.json");
    }

    public string RootDirectory => _directory;
    public string Path { get; }

    public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);
}
