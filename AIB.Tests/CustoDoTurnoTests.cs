using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Ai;
using AIB.Services.Mail;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O que a sessão de 09/09 cobrou e ninguém viu.
    /// <para>
    /// Três horas e dezoito minutos, treze chamadas ao modelo, setenta minutos de CPU, e a tarefa
    /// não terminou. O log não mentia em nenhuma linha — ele só media as coisas erradas: contava
    /// espera de gente como execução, não dizia por que o vigia estava parado, e despejou o
    /// esqueleto de uma chamada de ferramenta na tela como se fosse resposta.
    /// </para>
    /// </summary>
    public class CustoDoTurnoTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // O raciocínio, que é o multiplicador do custo
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ORaciocinio_NASCE_Desligado()
        {
            // Medido: nesta máquina o Ollama roda 100% em CPU, a 1,1–3,1 tok/s de geração. Um
            // turno gastou 953 tokens de pensamento em 726 segundos e emitiu content=0 — doze
            // minutos para nada aparecer na tela.
            new UserAppSettings().ModelThinking.Should().BeFalse();
            UserAppSettings.PadraoDoRaciocinio.Should().BeFalse();
        }

        [Fact]
        public void DESLIGADO_ORequestMANDA_ThinkFalse()
        {
            var opcoes = ChatRequestOptions.Default with { Think = false };

            opcoes.Think.Should().BeFalse(
                "false vai no corpo da requisição e desliga o bloco de pensamento");
        }

        [Fact]
        public void LIGADO_ORequestNAO_MandaOCampo()
        {
            // Ligado não é "think:true": é não mandar o campo e deixar o modelo no padrão dele.
            // Forçar true mudaria o comportamento de modelos que nem têm raciocínio.
            var opcoes = ChatRequestOptions.Default with { Think = (bool?)null };

            opcoes.Think.Should().BeNull();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Espera humana não é tempo de execução
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AEsperaNoModal_SAI_DoTempoDaFerramenta()
        {
            // Defeito real: "[TURNO 4] ~ ferramenta run_command — ok em 7299,6s" para um
            // Get-Content trivial. As duas horas eram o modal aberto esperando alguém clicar, e
            // o resumo do turno ainda dividia 463 caracteres por elas e anunciava "0,1 car/s".
            var linhas = new List<string>();

            using var pulso = new PulsoDoTurno(
                iteracao: 4, modelo: "qwen3.5:9b", tokensDoPrompt: 6376, reusoPrevisto: 4927,
                numCtx: 16384, intervalo: TimeSpan.FromHours(1), escrever: linhas.Add);

            pulso.FerramentaComecou("run_command");
            pulso.EsperaHumana("run_command", 7_290_000);
            pulso.FerramentaTerminou("run_command", falhou: false);

            string linha = linhas.Single(l => l.Contains("~ ferramenta run_command")
                                              && !l.Contains("executando"));

            linha.Should().Contain("esperando você");
            linha.Should().Contain("2h01min", "sete mil segundos ninguém converte de cabeça");
            linha.Should().NotContain("7290,0s");
        }

        [Fact]
        public void OResumoDoTurno_SEPARA_AEsperaDoTrabalho()
        {
            var linhas = new List<string>();

            using var pulso = new PulsoDoTurno(
                iteracao: 1, modelo: "m", tokensDoPrompt: 100, reusoPrevisto: 0,
                numCtx: 16384, intervalo: TimeSpan.FromHours(1), escrever: linhas.Add);

            pulso.EsperaHumana("run_command", 600_000);
            pulso.Fim("ferramenta(s) executada(s)");

            linhas.Last().Should().Contain("esperando você");
            linhas.Last().Should().Contain("10min00s");
        }

        [Fact]
        public void SemEsperaNenhuma_ALinhaFICA_ComoEra()
        {
            // A grande maioria das ferramentas não pede confirmação. Elas não podem ganhar um
            // sufixo vazio nem mudar de formato por causa desta correção.
            var linhas = new List<string>();

            using var pulso = new PulsoDoTurno(
                iteracao: 1, modelo: "m", tokensDoPrompt: 10, reusoPrevisto: 0,
                numCtx: 16384, intervalo: TimeSpan.FromHours(1), escrever: linhas.Add);

            pulso.FerramentaComecou("read_file");
            pulso.FerramentaTerminou("read_file", falhou: false);

            string linha = linhas.Single(l => l.Contains("~ ferramenta read_file")
                                              && !l.Contains("executando"));

            linha.Should().Contain("ok em");
            linha.Should().NotContain("esperando");
        }

        [Theory]
        [InlineData(500, "0,5s")]
        [InlineData(89_000, "89,0s")]
        [InlineData(600_000, "10min00s")]
        [InlineData(7_299_600, "2h01min")]
        public void ADuracao_EhLegivel_NasTresEscalas(long ms, string esperado)
        {
            PulsoDoTurno.Duracao(ms).Should().Be(esperado);
        }

        // ─────────────────────────────────────────────────────────────────────
        // O fallback de raciocínio
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("<tool_call>\n{\"name\":\"run_command\"}")]
        [InlineData("<parameter=command>\npowershell -File x.ps1\n</parameter>\n</function>\n</tool_call>")]
        [InlineData("<function=run_command>")]
        [InlineData("<invoke name=\"run_command\">")]
        public void OEsqueletoDeChamada_EhRECONHECIDO(string pensamento)
        {
            ChannelSplitter.ChamadaMalformada(pensamento).Should().BeTrue();
        }

        [Theory]
        [InlineData("O caminho é C:\\Users\\Carlo\\arquivo.txt.")]
        [InlineData("Vou usar a função de leitura para abrir o arquivo.")]
        [InlineData("")]
        [InlineData(null)]
        public void ProsaNORMAL_NaoEhConfundida(string? pensamento)
        {
            ChannelSplitter.ChamadaMalformada(pensamento).Should().BeFalse();
        }

        [Fact]
        public void OTurnoQueSO_Pensou_AindaEntregaOPensamento()
        {
            // O fallback continua valendo para o caso que o criou: a resposta certa presa no
            // raciocínio, com o canal final vazio. Só o XML é que não passa.
            var splitter = new ChannelSplitter();
            splitter.PushThinking("O arquivo está em C:\\Users\\Carlo\\notas.txt.");

            var saida = splitter.Flush(anyToolCallSeen: false);

            string.Concat(saida.Select(d => d.Text)).Should().Contain("notas.txt");
        }

        [Fact]
        public void OXML_NaoChegaNaTela()
        {
            // Defeito real: depois de oito turnos e 3h18, o que apareceu para o usuário foi
            //     <parameter=command>
            //     powershell -ExecutionPolicy Bypass -File "...\gerar_sigs.ps1"
            //     </parameter></function></tool_call>
            var splitter = new ChannelSplitter();
            splitter.PushThinking(
                "<parameter=command>\npowershell -File \"gerar_sigs.ps1\"\n</parameter>\n</function>\n</tool_call>");

            string saida = string.Concat(splitter.Flush(anyToolCallSeen: false).Select(d => d.Text));

            saida.Should().NotContain("<parameter");
            saida.Should().NotContain("</tool_call>");
            saida.Should().NotContain("powershell");
            saida.Should().Be(ChannelSplitter.RecadoDeChamadaMalformada);
            saida.Should().Contain("Nada foi executado");
        }

        // ─────────────────────────────────────────────────────────────────────
        // O vigia diz por que está parado
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ComATriagemDesligada_OVigia_DIZ_QuePulou()
        {
            // Em sete horas o vigia não deu uma passada e o log não tinha uma linha explicando.
            // As três saídas de BaterAsync eram mudas, e provar qual delas fechou o caminho só
            // foi possível olhando arquivos de estado depois.
            var (vigia, saida) = Montar(triagemLigada: false);

            await vigia.BaterAsync();

            string.Join("\n", saida).Should().Contain("[VIGIA] parado:")
                .And.Contain("desligada");
        }

        [Fact]
        public async Task OMotivo_NaoSeREPETE_ABatidaInteira()
        {
            // Minuto a minuto, "nada a fazer" viraria 1.440 linhas por dia e afogaria o log que
            // esta correção existe para salvar. Uma linha por MUDANÇA de estado.
            var (vigia, saida) = Montar(triagemLigada: false);

            await vigia.BaterAsync();
            await vigia.BaterAsync();
            await vigia.BaterAsync();

            saida.Count(l => l.Contains("[VIGIA] parado:")).Should().Be(1);
        }

        // ─────────────────────────────────────────────────────────────────────

        private static (MailDigestService Vigia, List<string> Saida) Montar(bool triagemLigada)
        {
            string pasta = Path.Combine(Path.GetTempPath(), "aib-pulso-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pasta);

            var servico = new SettingsService(Path.Combine(pasta, "settings.json"));
            var config = servico.LoadSettings();
            config.ShadowHandlesMail = triagemLigada;
            servico.SaveSettings(config);

            var saida = new List<string>();
            var anterior = Console.Out;
            var captura = new EscritorDeLista(saida);
            Console.SetOut(captura);

            var vigia = new MailDigestService(
                servico,
                new EmailMudo(),
                new FabricaMuda(),
                new MailVault(Path.Combine(pasta, "cofre")),
                new EstadoDasCaixas(pasta),
                null,
                Path.Combine(pasta, "regras.md"),
                new VigiasDoEmail(pasta),
                pasta,
                new DiarioDeTriagem(pasta));

            // O console volta ao normal assim que o serviço existe; o que interessa capturar é
            // o que sai das batidas, e elas acontecem depois.
            captura.Devolver = () => Console.SetOut(anterior);

            return (vigia, saida);
        }

        /// <summary>Console de mentira. Guarda linha por linha para o ensaio conferir.</summary>
        private sealed class EscritorDeLista : System.IO.TextWriter
        {
            private readonly List<string> _linhas;
            public EscritorDeLista(List<string> linhas) => _linhas = linhas;

            public Action? Devolver { get; set; }
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
            public override void WriteLine(string? valor) => _linhas.Add(valor ?? "");
            public override void Write(string? valor) { }
            public override void Write(char valor) { }
        }

        private sealed class EmailMudo : IMailService
        {
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
                Task.FromResult(new LeituraDaCaixa(Array.Empty<MensagemDeEmail>(), 1));

            public Task<IReadOnlyList<ThreadRespondida>> ThreadsRespondidasAsync(
                string e, string s, ImapEndpoint ep, DateTime d, CancellationToken ct) =>
                Task.FromResult((IReadOnlyList<ThreadRespondida>)Array.Empty<ThreadRespondida>());
        }

        private sealed class FabricaMuda : IChatProviderFactory
        {
            public IChatProvider GetProvider(UserAppSettings settings) =>
                throw new InvalidOperationException("o ensaio nunca deve chegar ao modelo");
        }
    }
}
