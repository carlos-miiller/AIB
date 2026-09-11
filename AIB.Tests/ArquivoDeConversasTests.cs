using System;
using System.IO;
using System.Linq;
using AIB.Services.Mail;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O histórico por conversa em <c>~/.AIB/email/conversas/</c>.
    /// <para>
    /// O agrupamento por lote só enxerga UMA passada. A thread triada ontem que recebe resposta
    /// hoje chegaria como "1 mensagem" numa conversa de três: os campos de
    /// <c>tela-chat-v3.html §3.10</c> mentiriam a partir da segunda passada. É este arquivo que
    /// torna "3 respostas" verdadeiro.
    /// </para>
    /// </summary>
    public class ArquivoDeConversasTests : IDisposable
    {
        private readonly string _dir;
        private readonly ArquivoDeConversas _arquivo;

        public ArquivoDeConversasTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aib-conv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _arquivo = new ArquivoDeConversas(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static string Em(DateTime q) => ArquivoDeConversas.Agora(q);

        private static readonly DateTime Base_ = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

        private static EntradaDaConversa Deles(uint uid, DateTime quando,
                                               string urgencia = "Maxima", string resumo = "assinar") =>
            new(Em(quando), uid, "cliente@x.com", "Contrato", urgencia, resumo, false, "vigia");

        private static EntradaDaConversa Minha(DateTime quando) =>
            new(Em(quando), 0, "eu@casa.com", "", "", "", true, "enviados");

        // ─────────────────────────────────────────────────────────────────────
        // O que NUNCA entra
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ORegistro_NaoTEM_OndeGuardarOCorpo()
        {
            // A garantia é ESTRUTURAL, e não uma promessa no comentário: sem campo, nenhum
            // descuido futuro grava corpo de e-mail alheio em disco.
            typeof(EntradaDaConversa).GetProperties()
                .Select(p => p.Name)
                .Should().BeEquivalentTo(new[]
                {
                    "Em", "Uid", "De", "Assunto", "Urgencia", "Resumo", "Minha", "Origem"
                });
        }

        [Fact]
        public void APasta_EhUmHASH_ENaoOAssunto()
        {
            // Assunto em nome de diretório vaza conteúdo para qualquer listagem, backup ou
            // indexador — sem ninguém abrir arquivo nenhum.
            string chave = ArquivoDeConversas.Chave("eu@casa.com", "T1", 0);

            _arquivo.CaminhoDaConversa(chave).Should().NotContain("Contrato");
            ArquivoDeConversas.NomeDaPasta(chave).Should().MatchRegex("^[0-9a-f]{32}$");
        }

        [Fact]
        public void AChave_SEPARA_ContasEThreads()
        {
            // A mesma thread em duas caixas viraria um arquivo só.
            ArquivoDeConversas.Chave("a@x.com", "T1", 0)
                .Should().NotBe(ArquivoDeConversas.Chave("b@x.com", "T1", 0));

            // Sem thread, cada mensagem é a própria conversa.
            ArquivoDeConversas.Chave("a@x.com", "", 1)
                .Should().NotBe(ArquivoDeConversas.Chave("a@x.com", "", 2));
        }

        // ─────────────────────────────────────────────────────────────────────
        // O histórico
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AMesmaMensagem_NaoEntra_DuasVezes()
        {
            // A passada relê a janela inteira e veria a mesma mensagem de novo. Sem o dedupe,
            // "N respostas" cresceria a cada vinte minutos sem ninguém escrever nada.
            string chave = ArquivoDeConversas.Chave("eu@casa.com", "T1", 0);

            _arquivo.Anotar(chave, Deles(41, Base_)).Should().BeTrue();
            _arquivo.Anotar(chave, Deles(41, Base_)).Should().BeFalse();

            _arquivo.Ler(chave).Should().HaveCount(1);
        }

        [Fact]
        public void RECARREGAR_Acrescenta_MesmoNaMesmaMensagem()
        {
            // Ali o veredito pode ter mudado, e guardar a mudança é o ponto: a regra 6 pede
            // poder conferir quando a triagem mudou de ideia.
            string chave = ArquivoDeConversas.Chave("eu@casa.com", "T1", 0);

            _arquivo.Anotar(chave, Deles(41, Base_));
            _arquivo.Anotar(chave, Deles(41, Base_, "Media", "menos urgente do que parecia")
                                       with { Origem = "recarregar" });

            _arquivo.Ler(chave).Should().HaveCount(2);
            _arquivo.EstadoDe(chave)!.Mensagens.Should().Be(1, "é a MESMA mensagem, relida");
        }

        [Fact]
        public void DuasRespostasSUAS_NaMesmaConversa_AmbasEntram()
        {
            // A pasta de Enviados não devolve uid: todas as entradas suas teriam uid 0. Sem a
            // identidade por carimbo, a SEGUNDA resposta virava repetição da primeira e a data
            // da conversa parava no seu primeiro envio.
            string chave = ArquivoDeConversas.Chave("eu@casa.com", "T1", 0);

            _arquivo.Anotar(chave, Minha(Base_.AddHours(1))).Should().BeTrue();
            _arquivo.Anotar(chave, Minha(Base_.AddHours(3))).Should().BeTrue();

            _arquivo.EstadoDe(chave)!.Mensagens.Should().Be(2);
            _arquivo.EstadoDe(chave)!.UltimaEm.Should().BeCloseTo(Base_.AddHours(3), TimeSpan.FromSeconds(1));
        }

        // ─────────────────────────────────────────────────────────────────────
        // O estado que a lista mostra
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ATRAVESSA_Passadas_ContandoAConversaInteira()
        {
            // O caso que motivou o arquivo: triada ontem, resposta hoje.
            string chave = ArquivoDeConversas.Chave("eu@casa.com", "T1", 0);

            _arquivo.Anotar(chave, Deles(41, Base_.AddDays(-1)));
            _arquivo.Anotar(chave, Minha(Base_.AddHours(-5)));
            _arquivo.Anotar(chave, Deles(48, Base_));

            var estado = _arquivo.EstadoDe(chave)!;

            estado.Mensagens.Should().Be(3);
            estado.EsperandoVoce.Should().BeTrue("a última palavra é do outro lado");
        }

        [Fact]
        public void SeVOCE_EscreveuPorUltimo_AUrgenciaNaoSEPERDE()
        {
            // Responder não pode apagar o motivo de a conversa existir. Ela vira "Aguardando
            // retorno" e MANTÉM o veredito daquilo que está sendo respondido.
            string chave = ArquivoDeConversas.Chave("eu@casa.com", "T1", 0);

            _arquivo.Anotar(chave, Deles(41, Base_, "Maxima", "assinar até sexta"));
            _arquivo.Anotar(chave, Minha(Base_.AddHours(2)));

            var estado = _arquivo.EstadoDe(chave)!;

            estado.EsperandoVoce.Should().BeFalse();
            estado.Urgencia.Should().Be("Maxima");
            estado.Resumo.Should().Be("assinar até sexta");
        }

        [Fact]
        public void ConversaSemArquivo_NaoTemEstado()
        {
            // Nova: o lote da passada já basta, e inventar um estado vazio faria a lista mostrar
            // "0 mensagens" numa conversa que acabou de chegar.
            _arquivo.EstadoDe(ArquivoDeConversas.Chave("eu@casa.com", "NOVA", 0)).Should().BeNull();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Retenção — o oposto da regra do raw.jsonl
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ZERO_DIAS_VarreTudo()
        {
            // MailJournalDays = 0 é a regra 3 estrita de volta: nada de e-mail em disco.
            string chave = ArquivoDeConversas.Chave("eu@casa.com", "T1", 0);
            _arquivo.Anotar(chave, Deles(41, Base_));

            Directory.Exists(_arquivo.Pasta).Should().BeTrue();

            _arquivo.Anotar(chave, Deles(42, Base_), diasMantidos: 0).Should().BeFalse();

            Directory.Exists(_arquivo.Pasta).Should().BeFalse();
        }

        [Fact]
        public void ConversaPARADA_EhApagada_PelaDataDaUltimaEntrada()
        {
            // Pela ÚLTIMA ENTRADA, e não pelo carimbo do arquivo: copiar a pasta de dados
            // atualizaria o carimbo do sistema e ressuscitaria conversas de meses atrás.
            string velha = ArquivoDeConversas.Chave("eu@casa.com", "VELHA", 0);
            string nova = ArquivoDeConversas.Chave("eu@casa.com", "NOVA", 0);

            _arquivo.Anotar(velha, Deles(1, Base_.AddDays(-30)));
            _arquivo.Anotar(nova, Deles(2, Base_));

            _arquivo.Limpar(diasMantidos: 7, agoraUtc: Base_);

            _arquivo.EstadoDe(velha).Should().BeNull();
            _arquivo.EstadoDe(nova).Should().NotBeNull();
            _arquivo.Quantas.Should().Be(1);
        }

        [Fact]
        public void TerHISTORICO_EhMotivoDeVIGIA()
        {
            // Sem isto, a resposta a uma conversa já triada podia ser descartada pelo funil —
            // mala-direta, envio em massa, remetente desconhecido — e o arquivo ficaria parado
            // sem ninguém notar: a tela mostraria uma contagem que parou de crescer e nenhum
            // sinal de que algo foi perdido.
            _arquivo.Anotar(ArquivoDeConversas.Chave("eu@casa.com", "T1", 0), Deles(41, Base_));
            _arquivo.Anotar(ArquivoDeConversas.Chave("eu@casa.com", "T2", 0), Deles(42, Base_));

            _arquivo.ThreadsComHistorico().Should().BeEquivalentTo(new[] { "T1", "T2" });
        }

        [Fact]
        public void ConversaSEM_Thread_NaoEntraNaVigia()
        {
            // Provedor que não expõe X-GM-THRID: cada mensagem é a própria conversa e não
            // existe "a resposta" a vigiar.
            _arquivo.Anotar(ArquivoDeConversas.Chave("eu@casa.com", "", 41), Deles(41, Base_));

            _arquivo.ThreadsComHistorico().Should().BeEmpty();
        }

        [Fact]
        public void AChaveEmClaro_NaoCarrega_Conteudo()
        {
            // Ela existe para desfazer o hash, e só. Nenhum assunto, nenhum remetente — nada
            // que uma listagem de diretório revele.
            string chave = ArquivoDeConversas.Chave("eu@casa.com", "T1", 0);
            _arquivo.Anotar(chave, Deles(41, Base_, resumo: "assinar o contrato Vertex"));

            string arquivo = Path.Combine(
                Path.GetDirectoryName(_arquivo.CaminhoDaConversa(chave))!,
                ArquivoDeConversas.NomeDaChave);

            string texto = File.ReadAllText(arquivo);
            texto.Should().Be(chave);
            texto.Should().NotContain("Contrato").And.NotContain("Vertex");
        }

        [Fact]
        public void LinhaCORROMPIDA_NaoDerruba_OArquivoInteiro()
        {
            // Queda no meio de uma escrita deixa exatamente uma linha truncada. Perder a
            // conversa inteira por causa dela seria o pior desfecho — mesma política do raw.
            string chave = ArquivoDeConversas.Chave("eu@casa.com", "T1", 0);
            _arquivo.Anotar(chave, Deles(41, Base_));

            File.AppendAllText(_arquivo.CaminhoDaConversa(chave), "{\"Em\":\"2026" + Environment.NewLine);

            _arquivo.Ler(chave).Should().HaveCount(1);
        }
    }
}
