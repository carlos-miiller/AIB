using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Ai;
using AIB.Services.Mail;
using AIB.Services.Tools;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A conversa perguntando ao vigia — o diário em disco e a ferramenta que o lê.
    /// <para>
    /// O valor destes ensaios está em duas coisas. A primeira é o CORPO: ele não pode chegar ao
    /// disco por caminho nenhum, e isso é o que sobrou da regra 3 como regra absoluta depois que
    /// o diário passou a gravar veredito. A segunda é o modelo NÃO INVENTAR: as respostas da
    /// ferramenta são montadas por código a partir de números apurados, e ela diz quando não
    /// sabe em vez de calar.
    /// </para>
    /// </summary>
    public class ConsultaDeEmailsTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // Andaimes
        // ─────────────────────────────────────────────────────────────────────

        private static string PastaNova()
        {
            string p = Path.Combine(Path.GetTempPath(), "aib-diario-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(p);
            return p;
        }

        private static EmailTriado Triado(
            string assunto = "Contrato", string urgencia = "media",
            string remetente = "ana@empresa.com", string nome = "Ana Souza",
            string resumo = "responder até sexta", DateTime? recebida = null)
            => new(remetente, nome, assunto, resumo, urgencia, "eu@empresa.com",
                   (recebida ?? DateTime.UtcNow).ToString("o"));

        private static PassadaAnotada Passada(
            DateTime? quando = null, int lidas = 10, int descartadas = 6,
            params EmailTriado[] triados)
            => new((quando ?? DateTime.UtcNow).ToString("o"), lidas, descartadas,
                   triados.Length > 0 ? triados : new[] { Triado() });

        // ─────────────────────────────────────────────────────────────────────
        // O diário
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ODiario_GRAVA_ELE_DeVolta()
        {
            string pasta = PastaNova();
            var diario = new DiarioDeTriagem(pasta);

            diario.Gravar(Passada(lidas: 31, descartadas: 27, triados: Triado(assunto: "Contrato")));

            var hoje = diario.Ler(DateTime.Now);

            hoje.Should().HaveCount(1);
            hoje[0].Lidas.Should().Be(31);
            hoje[0].Descartadas.Should().Be(27);
            hoje[0].Triados.Should().ContainSingle(t => t.Assunto == "Contrato");
        }

        [Fact]
        public void DuasPassadasNoMESMO_Dia_SOMAM_NoMesmoArquivo()
        {
            // O digest das 8h25 nao pode ser apagado pelo das 12h55: "quantos e-mails hoje" e a
            // soma do dia, e o Ultimo em memoria so guarda o mais recente.
            string pasta = PastaNova();
            var diario = new DiarioDeTriagem(pasta);

            diario.Gravar(Passada(lidas: 20));
            diario.Gravar(Passada(lidas: 11));

            var hoje = diario.Ler(DateTime.Now);

            hoje.Should().HaveCount(2);
            hoje.Sum(p => p.Lidas).Should().Be(31);
            Directory.GetFiles(Path.Combine(pasta, "email", "diario")).Should().HaveCount(1);
        }

        [Fact]
        public void OArquivoEhNomeadoPelaDataLOCAL_NaoPelaUtc()
        {
            // Quem pergunta "hoje" pensa no calendario da parede. Um digest das 22h no Brasil ja
            // e o dia seguinte em UTC, e cairia num arquivo que "hoje" nunca abriria.
            string pasta = PastaNova();
            var diario = new DiarioDeTriagem(pasta);

            var agora = DateTime.Now;
            diario.Gravar(Passada(quando: agora.ToUniversalTime()));

            File.Exists(diario.CaminhoDoDia(agora)).Should().BeTrue();
            Path.GetFileName(diario.CaminhoDoDia(agora))
                .Should().Be($"diario-{agora:yyyy-MM-dd}.json");
        }

        [Fact]
        public void OCORPO_NuncaChegaAoDisco()
        {
            // A regra 3 encolheu quando o diario passou a gravar veredito, mas nao sumiu: o
            // corpo continua vivendo so na memoria de uma triagem. O EmailTriado nao tem o
            // campo, entao nao ha descuido possivel — e este ensaio le o arquivo cru para
            // provar que ninguem o acrescentou depois.
            string pasta = PastaNova();
            var diario = new DiarioDeTriagem(pasta);

            diario.Gravar(Passada(triados: Triado(
                assunto: "Renovação", resumo: "assinar até sexta")));

            string cru = File.ReadAllText(diario.CaminhoDoDia(DateTime.Now));

            cru.Should().Contain("Renovação", "assunto é veredito, e veredito pode — e o arquivo " +
                                 "é para gente ler, então o acento não vem escapado");
            cru.Should().NotContain("Corpo");
            cru.Should().NotContain("corpo");
        }

        [Fact]
        public void ZERO_Dias_APAGA_OQueJaEstavaGravado()
        {
            // O mostrador em zero e "nao quero isto em disco", e nao "pare de gravar daqui pra
            // frente". Deixar o de ontem para tras faria a ferramenta continuar respondendo.
            string pasta = PastaNova();
            var diario = new DiarioDeTriagem(pasta);

            diario.Gravar(Passada());
            diario.Ler(DateTime.Now).Should().NotBeEmpty();

            diario.Gravar(Passada(), diasMantidos: 0);

            diario.Ler(DateTime.Now).Should().BeEmpty();
            Directory.Exists(diario.Pasta).Should().BeFalse();
        }

        [Fact]
        public void OPeriodo_ATRAVESSA_Dias()
        {
            string pasta = PastaNova();
            var diario = new DiarioDeTriagem(pasta);

            diario.Gravar(Passada(quando: DateTime.UtcNow.AddDays(-2), lidas: 5));
            diario.Gravar(Passada(quando: DateTime.UtcNow.AddDays(-1), lidas: 7));
            diario.Gravar(Passada(lidas: 9));

            diario.Ler(DateTime.Now).Should().HaveCount(1);
            diario.LerPeriodo(DateTime.Now.AddDays(-6), DateTime.Now).Sum(p => p.Lidas).Should().Be(21);
        }

        [Fact]
        public void ArquivoCORROMPIDO_NaoDerrubaALeitura()
        {
            string pasta = PastaNova();
            var diario = new DiarioDeTriagem(pasta);

            diario.Gravar(Passada());
            File.WriteAllText(diario.CaminhoDoDia(DateTime.Now), "{ isto nao e json");

            diario.Ler(DateTime.Now).Should().BeEmpty("nao lanca, so nao tem o que dizer");
        }

        // ─────────────────────────────────────────────────────────────────────
        // A ferramenta
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ComATriagemDESLIGADA_ElaDizISSO_EmVezDeCalar()
        {
            // O pior desfecho e responder "nao ha nada urgente" quando na verdade nada foi lido.
            string pasta = PastaNova();
            var servico = new SettingsService(Path.Combine(pasta, "settings.json"));
            var config = servico.LoadSettings();
            config.ShadowHandlesMail = false;
            servico.SaveSettings(config);

            var tool = new ConsultarEmailsTool(servico, new DiarioDeTriagem(pasta));

            string resposta = await tool.ExecuteAsync("{}");

            resposta.Should().Contain("DESLIGADA");
            resposta.Should().NotContain("urgente");
        }

        [Fact]
        public async Task SemNENHUMA_Triagem_ElaDizQuandoOsDigestsRodam()
        {
            string pasta = PastaNova();
            var servico = new SettingsService(Path.Combine(pasta, "settings.json"));
            var config = servico.LoadSettings();
            config.ShadowHandlesMail = true;
            config.MailAccounts.Add(new MailAccountSettings { Address = "eu@empresa.com" });
            servico.SaveSettings(config);

            var tool = new ConsultarEmailsTool(servico, new DiarioDeTriagem(pasta));

            string resposta = await tool.ExecuteAsync("{}");

            resposta.Should().Contain("Nenhuma triagem registrada");
            resposta.Should().Contain("08:25");
        }

        [Fact]
        public void ARespostaABRE_ComOsNumerosEComAHoraDaUltimaPassada()
        {
            // "quantos e-mails foram tratados hoje" tem de sair da primeira linha, sem o modelo
            // precisar contar itens de lista.
            var passadas = new[]
            {
                Passada(lidas: 20, descartadas: 17,
                        triados: new[] { Triado(urgencia: "baixa"), Triado(urgencia: "media") }),
                Passada(lidas: 11, descartadas: 10, triados: Triado(urgencia: "maxima"))
            };

            string r = ConsultarEmailsTool.Responder(passadas, "de hoje");

            r.Should().Contain("2 passada(s)");
            r.Should().Contain("31 mensagem(ns) lida(s)");
            r.Should().Contain("27 descartada(s)");
            r.Should().Contain("3 efetivamente triada(s)");
            r.Should().Contain("1 máxima, 1 média, 1 baixa");
            r.Should().Contain("Última passada");
        }

        [Fact]
        public void ABAIXA_CONTA_ComoTratada_MesmoSemIrParaATela()
        {
            // A tela mostra o que pede acao; "quantos voce tratou" e sobre o trabalho feito. Uma
            // triagem que leu 20 e dispensou 19 tratou 20.
            var passadas = new[]
            {
                Passada(lidas: 20, descartadas: 0,
                        triados: Enumerable.Range(0, 19).Select(_ => Triado(urgencia: "baixa"))
                            .Append(Triado(urgencia: "maxima")).ToArray())
            };

            ConsultarEmailsTool.Responder(passadas, "de hoje")
                .Should().Contain("20 efetivamente triada(s)");
        }

        [Fact]
        public void OFiltroDeURGENCIA_TrazSoOQuePedeAcaoAgora()
        {
            var passadas = new[]
            {
                Passada(triados: new[]
                {
                    Triado(assunto: "Newsletter", urgencia: "baixa"),
                    Triado(assunto: "Contrato de locação", urgencia: "maxima",
                           resumo: "assinar hoje até 18h"),
                    Triado(assunto: "Reunião", urgencia: "media")
                })
            };

            string r = ConsultarEmailsTool.Responder(passadas, "de hoje", urgencia: "maxima");

            r.Should().Contain("Contrato de locação");
            r.Should().Contain("assinar hoje até 18h");
            r.Should().NotContain("Newsletter");
            r.Should().NotContain("Reunião");
        }

        [Fact]
        public void OFiltroDeREMETENTE_OlhaONomeEOEndereco()
        {
            var passadas = new[]
            {
                Passada(triados: new[]
                {
                    Triado(assunto: "Proposta", remetente: "joao@fornecedor.com", nome: "João Lima"),
                    Triado(assunto: "Outra coisa", remetente: "ana@empresa.com", nome: "Ana Souza")
                })
            };

            ConsultarEmailsTool.Responder(passadas, "de hoje", remetente: "joão")
                .Should().Contain("Proposta").And.NotContain("Outra coisa");

            ConsultarEmailsTool.Responder(passadas, "de hoje", remetente: "fornecedor.com")
                .Should().Contain("Proposta").And.NotContain("Outra coisa");
        }

        [Fact]
        public void SemNadaQueCASE_ElaDizQueNaoAchou_SemInventar()
        {
            var passadas = new[] { Passada(triados: Triado(urgencia: "baixa")) };

            string r = ConsultarEmailsTool.Responder(passadas, "de hoje", urgencia: "maxima");

            r.Should().Contain("Nenhuma mensagem triada");
            r.Should().Contain("máxima");
            r.Should().Contain("1 efetivamente triada(s)", "os números do dia continuam lá");
        }

        [Fact]
        public void AMAIS_UrgenteVemPRIMEIRO_PorqueOTetoCorta()
        {
            var muitas = Enumerable.Range(0, ConsultarEmailsTool.TetoDaLista + 5)
                .Select(i => Triado(assunto: $"Ruído {i}", urgencia: "baixa"))
                .Append(Triado(assunto: "Contrato", urgencia: "maxima"))
                .ToArray();

            string r = ConsultarEmailsTool.Responder(new[] { Passada(triados: muitas) }, "de hoje");

            r.Should().Contain("Contrato", "o que corta a lista nunca pode ser o mais urgente");
            r.Should().Contain("não listada(s)");
        }

        [Theory]
        [InlineData("hoje", 0, "de hoje")]
        [InlineData("ontem", -1, "de ontem")]
        [InlineData("semana", -6, "dos últimos 7 dias")]
        [InlineData("", 0, "de hoje")]
        [InlineData("qualquer bobagem", 0, "de hoje")]
        public void OPeriodo_EhLidoOuVira_Hoje(string pedido, int diasAtras, string rotulo)
        {
            var agora = new DateTime(2026, 9, 4, 15, 0, 0, DateTimeKind.Local);

            var (de, ate, r) = ConsultarEmailsTool.Intervalo(pedido, agora);

            de.Should().Be(agora.Date.AddDays(diasAtras));
            ate.Should().Be(pedido == "ontem" ? agora.Date.AddDays(-1) : agora.Date);
            r.Should().Be(rotulo);
        }

        // ─────────────────────────────────────────────────────────────────────
        // O contexto que vai no system prompt
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OContextoNaoDizNADA_QuandoATriagemEstaDesligada()
        {
            var config = new UserAppSettings { ShadowHandlesMail = false };

            ConversationService.EstadoDoVigia(config, new DiarioDeTriagem(PastaNova()), DateTime.Now)
                .Should().BeEmpty("prompt de sistema e pago em prefill a cada requisicao");
        }

        [Fact]
        public void OContexto_AVISA_QuandoAindaNaoHouveTriagemHoje()
        {
            // Sem esta frase o modelo responderia "nada urgente hoje" com base em nada.
            var config = new UserAppSettings { ShadowHandlesMail = true };

            ConversationService.EstadoDoVigia(config, new DiarioDeTriagem(PastaNova()), DateTime.Now)
                .Should().Contain("Nenhuma triagem hoje ainda");
        }

        [Fact]
        public void OContextoLevaOsNUMEROS_ENUNCA_OConteudo()
        {
            // O system prompt entra em TODA requisicao e e o texto que a compactacao carrega
            // para dentro dos capitulos. Assunto e remetente aqui criariam a raiz permanente
            // que a regra 3 evita — os detalhes sao da ferramenta, chamada so quando perguntam.
            string pasta = PastaNova();
            var diario = new DiarioDeTriagem(pasta);

            diario.Gravar(Passada(lidas: 31, descartadas: 27, triados: new[]
            {
                Triado(assunto: "Contrato de locação", remetente: "joao@fornecedor.com",
                       nome: "João Lima", urgencia: "maxima", resumo: "assinar hoje"),
                Triado(assunto: "Newsletter", urgencia: "baixa")
            }));

            var config = new UserAppSettings { ShadowHandlesMail = true };

            string contexto = ConversationService.EstadoDoVigia(config, diario, DateTime.Now);

            contexto.Should().Contain("31 lida(s)");
            contexto.Should().Contain("2 triada(s)");
            contexto.Should().Contain("1 de urgência máxima");
            contexto.Should().Contain("mail");

            contexto.Should().NotContain("Contrato");
            contexto.Should().NotContain("João");
            contexto.Should().NotContain("fornecedor.com");
            contexto.Should().NotContain("assinar");
        }

        [Fact]
        public void ComODiarioDesligado_OContextoDizQueNaoHaHistorico()
        {
            var config = new UserAppSettings { ShadowHandlesMail = true, MailJournalDays = 0 };

            string contexto = ConversationService.EstadoDoVigia(
                config, new DiarioDeTriagem(PastaNova()), DateTime.Now);

            contexto.Should().Contain("registro das triagens está desligado");
        }

        // ─────────────────────────────────────────────────────────────────────
        // De ponta a ponta
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ODigest_ESCREVE_ODiarioQueAFerramentaLE()
        {
            string pasta = PastaNova();

            var servico = new SettingsService(Path.Combine(pasta, "settings.json"));
            var config = servico.LoadSettings();
            config.ShadowHandlesMail = true;
            config.MailAccounts.Add(new MailAccountSettings { Address = "eu@empresa.com" });
            servico.SaveSettings(config);

            var cofre = new MailVault(Path.Combine(pasta, "cofre"));
            cofre.Guardar("eu@empresa.com", "senha-de-app");

            var lidas = new[]
            {
                new MensagemDeEmail(1, "t1", "joao@fornecedor.com", "João Lima",
                    "Contrato de locação", DateTime.UtcNow, true, false,
                    Array.Empty<string>(), true, "", false, "eu@empresa.com"),
                new MensagemDeEmail(2, "t2", "ana@empresa.com", "Ana Souza",
                    "Almoço", DateTime.UtcNow, true, false,
                    Array.Empty<string>(), true, "", false, "eu@empresa.com")
            };

            var diario = new DiarioDeTriagem(pasta);

            var vigia = new MailDigestService(
                servico,
                new EmailQueDevolve(lidas),
                new FabricaDeProvider(new ProviderQueResponde(
                    "[{\"uid\":1,\"urgencia\":\"maxima\",\"resumo\":\"assinar hoje\"}," +
                    "{\"uid\":2,\"urgencia\":\"baixa\",\"resumo\":\"convite\"}]")),
                cofre,
                new EstadoDasCaixas(pasta),
                null,
                Path.Combine(pasta, "regras.md"),
                new VigiasDoEmail(pasta),
                pasta,
                diario);

            await vigia.ExecutarAsync(comModelo: true, CancellationToken.None);

            var tool = new ConsultarEmailsTool(servico, diario);

            string tudo = await tool.ExecuteAsync("{}");
            tudo.Should().Contain("2 efetivamente triada(s)",
                                  "a de urgência baixa foi trabalho feito tanto quanto a outra");

            string urgentes = await tool.ExecuteAsync("{\"urgencia\":\"maxima\"}");
            urgentes.Should().Contain("Contrato de locação");
            urgentes.Should().Contain("assinar hoje");
            urgentes.Should().NotContain("Almoço");

            string doJoao = await tool.ExecuteAsync("{\"remetente\":\"fornecedor\"}");
            doJoao.Should().Contain("Contrato de locação").And.NotContain("Almoço");
        }

        // ─────────────────────────────────────────────────────────────────────

        private sealed class EmailQueDevolve : IMailService
        {
            private readonly IReadOnlyList<MensagemDeEmail> _lidas;
            public EmailQueDevolve(IReadOnlyList<MensagemDeEmail> lidas) => _lidas = lidas;

            public bool Disponivel => true;
            public string MotivoDaIndisponibilidade => "";

            public Task<MailLoginResult> TestLoginAsync(string e, string s, CancellationToken ct) =>
                Task.FromResult(new MailLoginResult(true, default, "", true));

            public Task<MailScanResult> VarrerAsync(
                string e, string s, ImapEndpoint ep, DateTime d, EstadoDaCaixa? g, CancellationToken ct) =>
                Task.FromResult(new MailScanResult(true, 0, 0, false, 1, 0, ""));

            public Task<LeituraDaCaixa> LerAsync(
                string e, string s, ImapEndpoint ep, DateTime d, EstadoDaCaixa? g,
                string eu, CancellationToken ct) =>
                Task.FromResult(new LeituraDaCaixa(_lidas, 1));

            public Task<IReadOnlyList<ThreadRespondida>> ThreadsRespondidasAsync(
                string e, string s, ImapEndpoint ep, DateTime d, CancellationToken ct) =>
                Task.FromResult((IReadOnlyList<ThreadRespondida>)Array.Empty<ThreadRespondida>());
        }

        private sealed class ProviderQueResponde : IChatProvider
        {
            private readonly string _resposta;
            public ProviderQueResponde(string resposta) => _resposta = resposta;

            public string Name => "falso";
            public string Model => "modelo-de-teste";

            public async IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatTool> t, ChatRequestOptions o,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await Task.CompletedTask;
                yield break;
            }

            public Task<ChatCompletionResult> CompleteAsync(
                IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatTool> t,
                ChatRequestOptions o, CancellationToken ct) =>
                Task.FromResult(new ChatCompletionResult(_resposta, null, null));

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class FabricaDeProvider : IChatProviderFactory
        {
            private readonly IChatProvider _provider;
            public FabricaDeProvider(IChatProvider provider) => _provider = provider;
            public IChatProvider GetProvider(UserAppSettings settings) => _provider;
        }
    }
}
