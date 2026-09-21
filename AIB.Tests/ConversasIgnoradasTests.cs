using System;
using System.IO;
using AIB.Services;
using AIB.Services.Mail;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O "Ignorar" da caixa de entrada (§3.10).
    /// <para>
    /// Duas promessas estão travadas aqui. A conversa sai da tela e continua fora depois de
    /// reiniciar. E ela VOLTA quando chega mensagem nova — ignorar é arrumar o que já foi visto,
    /// não silenciar para sempre o que ainda nem chegou.
    /// </para>
    /// </summary>
    public class ConversasIgnoradasTests : IDisposable
    {
        private readonly string _raiz =
            Path.Combine(Path.GetTempPath(), "AIB-testes-ignoradas", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true); } catch { }
        }

        private static readonly DateTime Chegou = new(2026, 9, 15, 9, 30, 0, DateTimeKind.Utc);

        private static MailSummary Item(string thread, DateTime ultima, string assunto = "Contrato") =>
            new(assunto, "Pedem assinatura.", MailUrgency.Maxima,
                Account: "eu@gmail.com", LastMessageAt: ultima.ToLocalTime(), ThreadId: thread);

        [Fact]
        public void IgnoradaSaiDaLista_EAsOutrasFicam()
        {
            var ignoradas = new ConversasIgnoradas(_raiz);
            ignoradas.Ignorar(Item("1", Chegou));

            var (visiveis, quantas) = ignoradas.Filtrar(new[] { Item("1", Chegou), Item("2", Chegou) });

            quantas.Should().Be(1);
            visiveis.Should().ContainSingle().Which.ThreadId.Should().Be("2");
        }

        [Fact]
        public void MensagemNovaNaConversa_TrazDeVolta()
        {
            // A triagem erra para o lado de mostrar. Uma resposta nova é informação que o usuário
            // ainda não viu, e esconder essa também faria do clique um silenciamento permanente.
            var ignoradas = new ConversasIgnoradas(_raiz);
            ignoradas.Ignorar(Item("1", Chegou));

            ignoradas.Filtrar(new[] { Item("1", Chegou.AddMinutes(20)) })
                .Visiveis.Should().ContainSingle();
        }

        [Fact]
        public void SobreviveAoArranque()
        {
            new ConversasIgnoradas(_raiz).Ignorar(Item("1", Chegou));

            new ConversasIgnoradas(_raiz).Filtrar(new[] { Item("1", Chegou) })
                .Visiveis.Should().BeEmpty("o arquivo é a memória do clique");
        }

        [Fact]
        public void SemThread_OAssuntoNaoVaiEmClaroParaODisco()
        {
            new ConversasIgnoradas(_raiz).Ignorar(Item("", Chegou, assunto: "Contrato sigiloso"));

            string arquivo = File.ReadAllText(Path.Combine(_raiz, "email", ConversasIgnoradas.NomeDoArquivo));
            arquivo.Should().NotContain("sigiloso");
        }

        [Fact]
        public void ArquivoDoFormatoAntigo_ContinuaEscondendo_ESaiDoDiscoSemOAssunto()
        {
            // O formato antigo levava o assunto em claro. Migrar não pode devolver à tela o que o
            // usuário já tinha ignorado, e o assunto tem de sair do arquivo na primeira leitura.
            var item = Item("", Chegou, assunto: "Contrato sigiloso");
            string antiga = $"{item.Account}|sem-thread:{item.Name}|{Chegou:o}";
            string arquivo = Path.Combine(_raiz, "email", ConversasIgnoradas.NomeDoArquivo);
            Directory.CreateDirectory(Path.GetDirectoryName(arquivo)!);
            File.WriteAllText(arquivo, System.Text.Json.JsonSerializer.Serialize(
                new[] { new Ignorada(antiga, Chegou, Chegou) }));

            new ConversasIgnoradas(_raiz).Filtrar(new[] { item })
                .Visiveis.Should().BeEmpty("a linha migrada casa com a mesma mensagem");
            File.ReadAllText(arquivo).Should().NotContain("sigiloso");
        }

        [Fact]
        public void SemThread_IgnoraSoAquelaMensagem()
        {
            // Sem thread o resto do código usa conta|uid:0 — e todas as mensagens sem thread da
            // conta cairiam na mesma chave. Ignorar uma não pode ignorar todas.
            var ignoradas = new ConversasIgnoradas(_raiz);
            ignoradas.Ignorar(Item("", Chegou, "Boleto de setembro"));

            var (visiveis, _) = ignoradas.Filtrar(new[]
            {
                Item("", Chegou, "Boleto de setembro"),
                Item("", Chegou, "Reunião amanhã")
            });

            visiveis.Should().ContainSingle().Which.Name.Should().Be("Reunião amanhã");
        }

        [Fact]
        public void LinhaVelha_SaiDoArquivo()
        {
            var agora = Chegou;
            var ignoradas = new ConversasIgnoradas(_raiz, () => agora);

            ignoradas.Ignorar(Item("antiga", Chegou));

            agora = Chegou.AddDays(ConversasIgnoradas.DiasMantidos + 1);
            ignoradas.Ignorar(Item("nova", Chegou));

            ignoradas.Ler().Should().ContainSingle().Which.Chave.Should().EndWith("thr:nova");
        }

        [Fact]
        public void ArquivoIlegivel_NaoDerrubaNada()
        {
            var ignoradas = new ConversasIgnoradas(_raiz);
            Directory.CreateDirectory(Path.GetDirectoryName(ignoradas.Caminho)!);
            File.WriteAllText(ignoradas.Caminho, "{isto não é json");

            ignoradas.Filtrar(new[] { Item("1", Chegou) }).Visiveis.Should().ContainSingle();
        }
    }
}
