using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A ferramenta que roda uma habilidade.
    /// <para>
    /// Executa código na máquina do usuário, então passa pelo mesmo portão do
    /// <c>run_command</c>. O que o card mostra é o caminho literal do script: autorizar uma
    /// skill é autorizar aquele arquivo, e o usuário tem de poder abri-lo antes de dizer sim.
    /// </para>
    /// </summary>
    [Collection("Skills")]
    public class ExecuteSkillToolTests : IDisposable
    {
        private readonly string? _anterior = SkillService.SkillsDirectoryOverride;
        private readonly string _raiz;
        private readonly ExecuteSkillTool _tool = new();

        public ExecuteSkillToolTests()
        {
            _raiz = Path.Combine(Path.GetTempPath(), "aib-exec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_raiz);
            SkillService.SkillsDirectoryOverride = _raiz;
        }

        public void Dispose()
        {
            SkillService.SkillsDirectoryOverride = _anterior;
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private void Instalar(string nome, string interpretador, string? script = null, string corpo = "")
        {
            string pasta = Path.Combine(_raiz, nome);
            Directory.CreateDirectory(pasta);

            string cabecalho = $"---\nname: {nome}\ndescription: ensaio\ninterpreter: {interpretador}\n";
            if (script != null) cabecalho += "script_file: s.ps1\n";
            cabecalho += "---\n" + corpo;

            File.WriteAllText(Path.Combine(pasta, "SKILL.md"), cabecalho);
            if (script != null) File.WriteAllText(Path.Combine(pasta, "s.ps1"), script);
        }

        [Fact]
        public void AFerramenta_ExigeConfirmacao()
        {
            // Rodar uma skill é rodar código. Se isto virar false algum dia, o portão inteiro
            // deixa de existir para este caminho e nada mais avisa.
            _tool.RequiresConfirmation.Should().BeTrue();
        }

        [Fact]
        public void OCardMostra_OCaminhoDoScript()
        {
            Instalar("planilha", "powershell", script: "Write-Output 'x'");

            var ctx = _tool.BuildConfirmationContext(
                "{\"skill_name\":\"planilha\",\"arguments\":\"-Path a.xlsx\"}", userLevel: 5);

            ctx.Should().NotBeNull();
            ctx!.Command.Should().Contain("s.ps1").And.Contain("-Path a.xlsx");
            ctx.Tool.Should().Be("execute_skill");
        }

        [Fact]
        public void SkillInexistente_NaoViraPergunta()
        {
            // Convidar o usuário a autorizar algo que não existe é pedir um "sim" que não
            // executaria nada. Contexto nulo faz o registry recusar antes do card.
            _tool.BuildConfirmationContext("{\"skill_name\":\"fantasma\"}", userLevel: 5)
                .Should().BeNull();
        }

        [Fact]
        public async Task SkillInexistente_ListaAsQueExistem()
        {
            Instalar("alfa", "markdown", corpo: "corpo");

            string saida = await _tool.ExecuteAsync("{\"skill_name\":\"fantasma\"}");

            saida.Should().Contain("não encontrada");
            saida.Should().Contain("alfa", "o modelo precisa saber o que existe para se corrigir");
        }

        [Fact]
        public async Task SkillDeDocumentacao_DevolveOTexto()
        {
            // Interpretador markdown não roda nada: entrega as instruções. É como uma skill
            // ensina um procedimento sem automatizá-lo.
            Instalar("procedimento", "markdown", corpo: "Passo 1: respire.\nPasso 2: continue.");

            string saida = await _tool.ExecuteAsync("{\"skill_name\":\"procedimento\"}");

            saida.Should().Contain("Passo 1");
        }

        [Fact]
        public async Task InterpretadorDesconhecido_ERecusadoComNome()
        {
            Instalar("estranha", "brainfuck", script: "+++");

            string saida = await _tool.ExecuteAsync("{\"skill_name\":\"estranha\"}");

            saida.Should().Contain("não suportado");
        }

        [Fact]
        public async Task SemNome_ERecusado()
        {
            (await _tool.ExecuteAsync("{}")).Should().Contain("obrigatório");
        }

        [Fact]
        public async Task ParametroObrigatorioAusente_FalhaNaHoraEmVezDeTravar()
        {
            // O bug de verdade: a entrada do processo era herdada do console do app, entao o
            // PowerShell abria "Supply values for the following parameters" e ficava parado ate
            // o teto de 60 segundos. A llm chamou a skill de planilha sem -Path tres vezes e
            // esperou um minuto em cada uma, recebendo de volta "passou de 60 segundos" — uma
            // mensagem que nao diz nada sobre o parametro que faltou.
            Instalar("exigente", "powershell",
                script: "param([Parameter(Mandatory=$true)][string]$Obrigatorio) Write-Output $Obrigatorio");

            var relogio = System.Diagnostics.Stopwatch.StartNew();
            string saida = await _tool.ExecuteAsync("{\"skill_name\":\"exigente\"}");
            relogio.Stop();

            saida.Should().NotContain("passou de", "o prompt tem de ler EOF em vez de esperar alguem digitar");
            saida.Should().Contain("Obrigatorio", "o erro precisa nomear o parametro que falta");
            relogio.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        }

        [Fact]
        public async Task ChamadaQueFalha_RecebeAsInstrucoesDeUso()
        {
            // O prompt de sistema lista nome e descricao da skill, nada sobre os argumentos.
            // Sem devolver o corpo do SKILL.md no erro, o modelo so pode adivinhar a
            // assinatura de novo — que foi exatamente o loop de tres tentativas.
            Instalar("exigente", "powershell",
                script: "param([Parameter(Mandatory=$true)][string]$Obrigatorio) Write-Output $Obrigatorio",
                corpo: "Chame com -Obrigatorio \"texto\".");

            string saida = await _tool.ExecuteAsync("{\"skill_name\":\"exigente\"}");

            saida.Should().Contain("Chame com -Obrigatorio");
        }

        [Fact]
        public async Task ChamadaQueDaCerto_NaoCarregaAsInstrucoes()
        {
            // Contexto pago: mandar o SKILL.md junto de toda saida boa custaria o corpo inteiro
            // em cada chamada, e o modelo ja sabe usar a skill quando ela funcionou.
            Instalar("certeira", "powershell",
                script: "Write-Output 'pronto'",
                corpo: "ISTO NAO DEVE APARECER.");

            string saida = await _tool.ExecuteAsync("{\"skill_name\":\"certeira\"}");

            saida.Should().Contain("pronto");
            saida.Should().NotContain("ISTO NAO DEVE APARECER");
        }

        [Fact]
        public async Task ScriptPowerShell_RodaEDevolveASaida()
        {
            Instalar("eco", "powershell", script: "param([string]$Texto) Write-Output \"eco: $Texto\"");

            string saida = await _tool.ExecuteAsync(
                "{\"skill_name\":\"eco\",\"arguments\":\"-Texto ola\"}");

            saida.Should().Contain("eco: ola");
        }
    }
}
