using System;
using System.IO;
using AIB.Services.Ai;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>O arquivo do "Imprimir o prompt": o corpo, aberto em seções.</summary>
    public class RetratoDoEnvioTests
    {
        private const string Corpo =
            "{\"model\":\"gemma\",\"messages\":[" +
            "{\"role\":\"system\",\"content\":\"Voc\\u00EA \\u00E9 a Ayano.\\nRegra 1: n\\u00E3o invente.\"}," +
            "{\"role\":\"user\",\"content\":\"oi\"}]," +
            "\"stream\":true,\"keep_alive\":-1,\"think\":false," +
            "\"options\":{\"temperature\":0.1,\"num_ctx\":32768}," +
            "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"read\",\"description\":\"l\\u00EA\"}}]}";

        [Fact]
        public void ORetrato_ABRE_OCorpoEmSecoesLegiveis()
        {
            string texto = RetratoDoEnvio.Renderizar(Corpo, new DateTime(2026, 9, 14, 15, 30, 0));

            texto.Should().Contain("Nada foi enviado ao modelo");
            texto.Should().Contain("MENSAGEM 1/2 · system");
            // Quebra de linha e acento de verdade: é o texto que o modelo lê, e escapes no meio
            // dele não são o que ninguém quer ler.
            texto.Should().Contain("Você é a Ayano." + "\n" + "Regra 1: não invente.");
            texto.Should().Contain("MENSAGEM 2/2 · user");
            texto.Should().Contain("FERRAMENTAS · 1");
            texto.Should().Contain("\"name\": \"read\"");
            texto.Should().Contain("\"num_ctx\": 32768");
            texto.Should().Contain("keep_alive: -1");
        }

        [Fact]
        public void ORetrato_TERMINA_ComOCorpoCruIntacto()
        {
            RetratoDoEnvio.Renderizar(Corpo, DateTime.Now).TrimEnd().Should().EndWith(Corpo);
        }

        [Fact]
        public void CorpoIlegivel_AINDA_SaiCru()
        {
            string texto = RetratoDoEnvio.Renderizar("{quebrado", DateTime.Now);

            texto.Should().Contain("não consegui abrir o corpo em seções");
            texto.TrimEnd().Should().EndWith("{quebrado");
        }

        [Fact]
        public void OArquivo_NAO_CaiNaPodaDoRegistroDeExecucao()
        {
            string pasta = Path.Combine(Path.GetTempPath(), "aib-retrato-" + Guid.NewGuid().ToString("N"));
            try
            {
                string caminho = RetratoDoEnvio.Gravar("conteúdo ção", pasta, new DateTime(2026, 9, 14, 15, 30, 5));

                File.ReadAllText(caminho).Should().Be("conteúdo ção");
                Path.GetFileName(caminho).Should().Be("prompt-2026-09-14-153005.txt");

                // A poda apaga execucao-*.log.
                Directory.GetFiles(pasta, "execucao-*.log").Should().BeEmpty();
            }
            finally
            {
                try { Directory.Delete(pasta, true); } catch { }
            }
        }
    }
}
