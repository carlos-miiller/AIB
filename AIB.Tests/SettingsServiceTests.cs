using System;
using System.IO;
using Xunit;
using FluentAssertions;
using AIB.Services;

namespace AIB.Tests
{
    public class SettingsServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public SettingsServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AIB_SettingsServiceTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "profile.dat");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void SettingsPath_SemCaminhoExplicito_SegueDirectoryService()
        {
            var service = new SettingsService();
            service.SettingsPath.Should().Be(DirectoryService.SettingsPath);
        }

        [Fact]
        public void SettingsPath_ComCaminhoExplicito_UsaOCaminhoDado()
        {
            var service = new SettingsService(_path);
            service.SettingsPath.Should().Be(_path);
        }

        [Fact]
        public void SaveSettings_GravaCriptografado_ELeDeVolta()
        {
            var service = new SettingsService(_path);
            var settings = new UserAppSettings { ModelName = "modelo-x", MessageCount = 7 };

            service.SaveSettings(settings);

            File.Exists(_path).Should().BeTrue();
            // Criptografado: o JSON em claro não pode aparecer nos bytes.
            File.ReadAllBytes(_path).Should().NotBeEmpty();

            var reread = new SettingsService(_path).LoadSettings();
            reread.ModelName.Should().Be("modelo-x");
            reread.MessageCount.Should().Be(7);
        }

        [Fact]
        public void LoadSettings_ServeDoCache_MesmoComOArquivoApagado()
        {
            var service = new SettingsService(_path);
            service.SaveSettings(new UserAppSettings { ModelName = "cacheado" });

            var first = service.LoadSettings();
            File.Delete(_path);

            var second = service.LoadSettings();

            first.ModelName.Should().Be("cacheado");
            second.ModelName.Should().Be("cacheado");
            File.Exists(_path).Should().BeFalse("o cache não pode voltar ao disco");
        }

        [Fact]
        public void InvalidateCache_ForcaNovaLeituraDoDisco()
        {
            var service = new SettingsService(_path);
            service.SaveSettings(new UserAppSettings { ModelName = "antigo" });
            service.LoadSettings().ModelName.Should().Be("antigo");

            // Outra instância grava por baixo — o cache da primeira fica velho.
            new SettingsService(_path).SaveSettings(new UserAppSettings { ModelName = "novo" });
            service.LoadSettings().ModelName.Should().Be("antigo");

            service.InvalidateCache();
            service.LoadSettings().ModelName.Should().Be("novo");
        }

        [Fact]
        public void LoadSettings_DevolveCopia_MutacaoNaoVazaParaOCache()
        {
            var service = new SettingsService(_path);
            service.SaveSettings(new UserAppSettings { ModelName = "original" });

            var mutated = service.LoadSettings();
            mutated.ModelName = "adulterado";

            service.LoadSettings().ModelName.Should().Be("original");
        }

        [Fact]
        public void Clone_IsolaAInstancia()
        {
            var settings = new UserAppSettings { ModelName = "a", MessageCount = 1 };
            var clone = settings.Clone();

            clone.ModelName = "b";
            clone.MessageCount = 2;

            settings.ModelName.Should().Be("a");
            settings.MessageCount.Should().Be(1);
        }
    }
}
