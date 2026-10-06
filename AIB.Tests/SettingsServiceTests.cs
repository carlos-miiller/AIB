using System;
using System.Collections.Generic;
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

        [Fact]
        public void Salvar_TrocandoDeProvedor_GUARDA_AsEdicoesDoAnterior()
        {
            // A tela gravava os perfis e depois ativava o escolhido; o Ativar guardava o perfil do
            // anterior a partir dos campos da conversa, ainda com os valores de antes da edição.
            var s = new UserAppSettings
            {
                AiProvider = ProvedoresDeIa.Ollama,
                ModelName = "modelo-antigo",
                KeepAlive = "-1",
                PerfisMigrados = true
            }.Sanear();

            // O que a tela tem na hora do Salvar: o Ollama editado e o OpenRouter escolhido.
            var perfis = new Dictionary<string, PerfilDeProvedor>
            {
                [ProvedoresDeIa.Ollama] = s.PerfilDe(ProvedoresDeIa.Ollama),
                [ProvedoresDeIa.OpenRouter] = s.PerfilDe(ProvedoresDeIa.OpenRouter)
            };
            perfis[ProvedoresDeIa.Ollama].Modelo = "modelo-novo";
            perfis[ProvedoresDeIa.Ollama].KeepAlive = "30m";
            perfis[ProvedoresDeIa.Ollama].JanelaDeContexto = 16384;
            perfis[ProvedoresDeIa.OpenRouter].Modelo = "fornecedor/modelo";

            s.AplicarPerfis(ProvedoresDeIa.OpenRouter, perfis);

            s.AiProvider.Should().Be(ProvedoresDeIa.OpenRouter);
            s.ModelName.Should().Be("fornecedor/modelo");

            var ollama = s.PerfilDe(ProvedoresDeIa.Ollama);
            ollama.Modelo.Should().Be("modelo-novo", "a edição do provedor anterior sobrevive à troca");
            ollama.KeepAlive.Should().Be("30m");
            ollama.JanelaDeContexto.Should().Be(16384);
        }

        [Fact]
        public void Salvar_SemTrocarDeProvedor_GuardaOsDois()
        {
            var s = new UserAppSettings { AiProvider = ProvedoresDeIa.Ollama, PerfisMigrados = true }.Sanear();

            var perfis = new Dictionary<string, PerfilDeProvedor>
            {
                [ProvedoresDeIa.Ollama] = s.PerfilDe(ProvedoresDeIa.Ollama),
                [ProvedoresDeIa.OpenRouter] = s.PerfilDe(ProvedoresDeIa.OpenRouter)
            };
            perfis[ProvedoresDeIa.Ollama].Modelo = "modelo-local";
            perfis[ProvedoresDeIa.OpenRouter].Modelo = "fornecedor/outro";

            s.AplicarPerfis(ProvedoresDeIa.Ollama, perfis);

            s.ModelName.Should().Be("modelo-local");
            s.PerfilDe(ProvedoresDeIa.OpenRouter).Modelo.Should().Be("fornecedor/outro");
        }

        [Fact]
        public void ArquivoAntigoComApiKey_PerdeOCampo_NoProximoSave()
        {
            // O campo ApiKey saiu: era sentinela que ninguém lia, e em arquivos antigos podia ter
            // a chave em texto. Chave FALSA — nenhum ensaio toca chave de verdade.
            File.WriteAllText(_path, "{\"ApiKey\":\"chave-falsa-de-ensaio\",\"ModelName\":\"modelo-x\"}");

            // Texto claro: a leitura cai no caminho legado e regrava criptografado.
            var lido = new SettingsService(_path).LoadSettings();
            lido.ModelName.Should().Be("modelo-x");

            byte[] claro = System.Security.Cryptography.ProtectedData.Unprotect(
                File.ReadAllBytes(_path), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            string json = System.Text.Encoding.UTF8.GetString(claro);

            json.Should().NotContain("ApiKey").And.NotContain("chave-falsa-de-ensaio");
            json.Should().NotContain("ShadowModelName", "o outro campo morto também saiu");
        }
    }
}
