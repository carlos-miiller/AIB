using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O que fez a llm "nadar, nadar e morrer na praia".
    /// <para>
    /// Reconstituição do caso: o modelo pediu <c>ler-planilha -Path "…\TEMP\users.xls"</c>. O
    /// arquivo não existia — estava numa subpasta, e em CSV. A resposta foi "não encontrado"
    /// seguida do manual completo da habilidade, que explicava como CHAMÁ-LA, coisa que já
    /// estava certa. Ninguém disse o que havia na pasta. O modelo repetiu a mesma chamada
    /// quatro vezes, cada uma abrindo um modal de confirmação, e o manual foi anexado às quatro
    /// — quase quatro mil caracteres do mesmo texto empurrados para dentro do contexto.
    /// </para>
    /// </summary>
    public class PreVooDeSkillsTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // Achar caminho no meio dos argumentos
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OCaminho_EhACHADO_MesmoComEspacoNoNomeDaPasta()
        {
            // "emails fisio" é o nome real da pasta do caso. Um extrator que quebrasse no espaço
            // não veria o caminho e o pré-voo nunca dispararia justamente onde precisava.
            var achados = PreVooDeCaminho.Extrair(
                "-Path \"C:\\Users\\Carlo\\CPAPS\\TEMP\\emails fisio\\users.csv\" -MaxLinhas 50");

            achados.Should().ContainSingle()
                .Which.Should().Be("C:\\Users\\Carlo\\CPAPS\\TEMP\\emails fisio\\users.csv");
        }

        [Fact]
        public void SemCaminhoNenhum_NaoInventa()
        {
            PreVooDeCaminho.Extrair("-Filtro \"Fernando\" -MaxLinhas 10").Should().BeEmpty();
            PreVooDeCaminho.Extrair("").Should().BeEmpty();
            PreVooDeCaminho.Extrair(null).Should().BeEmpty();
        }

        // ─────────────────────────────────────────────────────────────────────
        // A resposta que teria encerrado o caso no primeiro turno
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ArquivoInexistente_RESPONDE_ComOQueExisteNaPasta()
        {
            string pasta = PastaNova();
            File.WriteAllText(Path.Combine(pasta, "users.csv"), "id;nome");
            File.WriteAllText(Path.Combine(pasta, "email carlos.html"), "<html>");

            string? recusa = PreVooDeCaminho.Conferir($"-Path \"{Path.Combine(pasta, "users.xls")}\"");

            recusa.Should().NotBeNull();
            recusa.Should().Contain("não existe");
            recusa.Should().Contain("users.csv", "é o que resolve, e é o que faltava dizer");
            recusa.Should().Contain("email carlos.html");
            recusa.Should().Contain("Mesmo nome, outra extensão",
                                    "o de mesmo radical vem apontado, não perdido na lista");
        }

        [Fact]
        public void PastaQueNaoExiste_ApontaAAncestralQueEXISTE()
        {
            string pasta = PastaNova();
            Directory.CreateDirectory(Path.Combine(pasta, "emails fisio"));

            string? recusa = PreVooDeCaminho.Conferir(
                $"-Path \"{Path.Combine(pasta, "nao", "existe", "x.xlsx")}\"");

            recusa.Should().NotBeNull();
            recusa.Should().Contain(pasta, "a pasta mais funda que existe de verdade");
            recusa.Should().Contain("emails fisio\\", "subpasta marcada como subpasta");
        }

        [Fact]
        public void ArquivoQueEXISTE_NaoAtrapalha()
        {
            string pasta = PastaNova();
            string arquivo = Path.Combine(pasta, "planilha.xlsx");
            File.WriteAllText(arquivo, "x");

            PreVooDeCaminho.Conferir($"-Path \"{arquivo}\"").Should().BeNull();
            PreVooDeCaminho.Conferir($"-Path \"{pasta}\"").Should().BeNull("pasta também é caminho válido");
        }

        // ─────────────────────────────────────────────────────────────────────
        // .xls não é .xlsx
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void FormatoNaoDECLARADO_EhRecusadoComExplicacao()
        {
            // Mesmo com o caminho certo, a habilidade teria falhado: .xls é OLE2 binário e .xlsx
            // é um ZIP de XML. A mensagem antiga seria outro "não consegui abrir", sem dizer que
            // o problema era o formato.
            string pasta = PastaNova();
            string velho = Path.Combine(pasta, "users.xls");
            File.WriteAllText(velho, "x");

            string? recusa = PreVooDeCaminho.Conferir($"-Path \"{velho}\"", new[] { ".xlsx" });

            recusa.Should().NotBeNull();
            recusa.Should().Contain(".xlsx");
            recusa.Should().Contain("renomear não resolve");
        }

        [Fact]
        public void SemDeclararNada_ASkillAceitaTudo_ComoAntes()
        {
            string pasta = PastaNova();
            string qualquer = Path.Combine(pasta, "coisa.zzz");
            File.WriteAllText(qualquer, "x");

            PreVooDeCaminho.Conferir($"-Path \"{qualquer}\"").Should().BeNull();
            PreVooDeCaminho.Conferir($"-Path \"{qualquer}\"", Array.Empty<string>()).Should().BeNull();
        }

        [Theory]
        [InlineData("xlsx", ".xlsx")]
        [InlineData(".xlsx", ".xlsx")]
        [InlineData("*.xlsx", ".xlsx")]
        public void OAccepts_ACEITA_AsTresFormasDeEscrever(string declarado, string esperado)
        {
            SkillService.Extensoes(declarado).Should().Equal(esperado);
        }

        [Fact]
        public void OAccepts_LE_ListaComVirgula()
        {
            SkillService.Extensoes(".xlsx, .xlsm ; csv").Should().Equal(".xlsx", ".xlsm", ".csv");
            SkillService.Extensoes("").Should().BeEmpty();
            SkillService.Extensoes(null).Should().BeEmpty();
        }

        // ─────────────────────────────────────────────────────────────────────
        // A habilidade, de ponta a ponta
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AHabilidade_RECUSA_AntesDeAbrirOModal()
        {
            // O pré-voo roda ANTES do portão de confirmação. Cada uma das quatro tentativas do
            // caso real abriu um modal pedindo autorização para executar algo que ia falhar de
            // qualquer jeito.
            string raiz = PastaNova();
            var tool = SkillDeMentira(raiz, accepts: ".xlsx");

            string args = JsonSerializer.Serialize(new
            {
                skill_name = "planilha",
                arguments = "-Path " + Aspas(Path.Combine(raiz, "nao-existe.xlsx"))
            });

            tool.Validar(args).Should().NotBeNull().And.Subject.As<string>()
                .Should().Contain("não existe");

            SkillService.SkillsDirectoryOverride = null;
        }

        [Fact]
        public void SemCaminho_AHabilidade_NaoRecusaNada()
        {
            string raiz = PastaNova();
            var tool = SkillDeMentira(raiz, accepts: "");

            string args = JsonSerializer.Serialize(new
            {
                skill_name = "planilha",
                arguments = "-Filtro Fernando"
            });

            tool.Validar(args).Should().BeNull();
            tool.Validar("{ isto nao e json").Should().BeNull("argumento ilegível é problema do executor");

            SkillService.SkillsDirectoryOverride = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Repetição
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AAssinatura_SEPARA_ChamadasComArgumentosDiferentes()
        {
            string a = AgentLoop.Assinatura("ler-planilha", "-Path \"C:\\a.xlsx\"");
            string b = AgentLoop.Assinatura("ler-planilha", "-Path \"C:\\b.xlsx\"");

            a.Should().NotBe(b, "argumento diferente é tentativa diferente, e essa passa");
            a.Should().Be(AgentLoop.Assinatura("ler-planilha", "-Path \"C:\\a.xlsx\""));
        }

        [Fact]
        public void ORecadoDeRepeticao_CARREGA_OErroAnterior()
        {
            // Bloquear sem dizer por quê seria trocar um laço por um beco sem saída. O recado
            // traz o erro de volta e nomeia as três saídas possíveis.
            string recado = AgentLoop.RecadoDeRepeticao(
                "ler-planilha", "ERRO: arquivo nao encontrado: C:\\...\\users.xls");

            recado.Should().Contain("já foi feita neste turno");
            recado.Should().Contain("arquivo nao encontrado");
            recado.Should().Contain("Não repita");
            recado.Should().Contain("Mude os argumentos");
        }

        [Fact]
        public void OErroAnterior_VaiRESUMIDO_NaoOManualInteiro()
        {
            // O manual da habilidade tem quase mil caracteres e vinha grudado no erro. Repeti-lo
            // a cada bloqueio desfaria a economia de contexto que o bloqueio existe para fazer.
            string gordo = "ERRO: arquivo nao encontrado: C:\\x.xls\n\n--- Como usar ---\n"
                           + new string('m', 2000);

            string curto = AgentLoop.Resumir(gordo);

            curto.Should().Be("ERRO: arquivo nao encontrado: C:\\x.xls");
            curto.Should().NotContain("Como usar");
        }

        [Fact]
        public void ErroDeUmaLinhaMuitoLONGA_EhCortado()
        {
            AgentLoop.Resumir(new string('x', 500)).Length.Should().BeLessThan(310);
        }

        // ─────────────────────────────────────────────────────────────────────
        // O manual não se repete
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task OManual_VemUmaVez_ERepeteSoUmPonteiro()
        {
            string raiz = PastaNova();
            var tool = SkillDeMentira(raiz, accepts: "", script: "exit 1");

            string args = JsonSerializer.Serialize(new { skill_name = "planilha", arguments = "" });

            string primeira = await tool.ExecuteAsync(args, 9);
            string segunda = await tool.ExecuteAsync(args, 9);

            primeira.Should().Contain("Como usar a habilidade 'planilha'");
            primeira.Should().Contain("MANUAL DE MENTIRA");

            segunda.Should().NotContain("MANUAL DE MENTIRA", "mil caracteres repetidos incham o prompt");
            segunda.Should().Contain("já foi enviado nesta sessão");

            SkillService.SkillsDirectoryOverride = null;
        }

        // ─────────────────────────────────────────────────────────────────────

        private static string PastaNova()
        {
            string p = Path.Combine(Path.GetTempPath(), "aib-skill-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(p);
            return p;
        }

        private static string Aspas(string texto) => (char)34 + texto + (char)34;

        /// <summary>Uma habilidade em disco, só para o ensaio. Nunca toca as do usuário.</summary>
        private static ExecuteSkillTool SkillDeMentira(string raiz, string accepts, string? script = null)
        {
            string pasta = Path.Combine(raiz, "_skills", "planilha");
            Directory.CreateDirectory(pasta);

            string cabecalho = "---\nname: planilha\ndescription: d\n"
                               + (accepts.Length > 0 ? $"accepts: {accepts}\n" : "")
                               + "interpreter: powershell\nscript_file: p.ps1\n---\n\nMANUAL DE MENTIRA\n";

            File.WriteAllText(Path.Combine(pasta, "SKILL.md"), cabecalho);
            File.WriteAllText(Path.Combine(pasta, "p.ps1"), script ?? "exit 0");

            SkillService.SkillsDirectoryOverride = Path.Combine(raiz, "_skills");
            return new ExecuteSkillTool();
        }
    }
}
