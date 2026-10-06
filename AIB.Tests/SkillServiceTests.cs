using System;
using System.IO;
using System.Linq;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A leitura das habilidades instaladas.
    /// <para>
    /// O <c>SkillService</c> era um stub que devolvia lista vazia e contagem zero. O bloco
    /// "Habilidades dinâmicas disponíveis" existia no prompt de sistema e nunca aparecia,
    /// porque a lista nunca tinha nada; havia skill em disco que nunca foi alcançável. Estes
    /// ensaios existem para que a volta ao estado de stub quebre alguma coisa.
    /// </para>
    /// </summary>
    [Collection("Skills")]
    public class SkillServiceTests : IDisposable
    {
        private readonly string? _anterior = SkillService.SkillsDirectoryOverride;
        private readonly string _raiz;

        public SkillServiceTests()
        {
            _raiz = Path.Combine(Path.GetTempPath(), "aib-skills-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_raiz);
            SkillService.SkillsDirectoryOverride = _raiz;
        }

        public void Dispose()
        {
            SkillService.SkillsDirectoryOverride = _anterior;
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private string Instalar(string pasta, string skillMd, string? script = null, string nomeScript = "s.ps1")
        {
            string destino = Path.Combine(_raiz, pasta);
            Directory.CreateDirectory(destino);
            File.WriteAllText(Path.Combine(destino, "SKILL.md"), skillMd);

            if (script != null) File.WriteAllText(Path.Combine(destino, nomeScript), script);

            return destino;
        }

        [Fact]
        public void CabecalhoCompleto_ViraHabilidade()
        {
            Instalar("planilha",
                "---\nname: ler-planilha\ndescription: Lê .xlsx\ninterpreter: powershell\n"
                + "script_file: s.ps1\n---\n\n## Como usar\n\nChame com -Path.\n",
                script: "Write-Output 'oi'");

            var skills = SkillService.ListLocalSkills();

            skills.Should().ContainSingle();
            skills[0].Name.Should().Be("ler-planilha");
            skills[0].Description.Should().Be("Lê .xlsx");
            skills[0].Interpreter.Should().Be("powershell");
            skills[0].ScriptPath.Should().EndWith("s.ps1");
            skills[0].Instructions.Should().Contain("Chame com -Path");
        }

        [Fact]
        public void SemNome_EIgnorada()
        {
            // O nome da pasta NÃO serve de substituto: as instruções dentro do arquivo se
            // referem ao nome declarado, e adivinhar produziria uma skill que o modelo chama
            // por um nome e que documenta outro.
            Instalar("sem-nome", "---\ndescription: falta o nome\ninterpreter: powershell\n---\n");

            SkillService.ListLocalSkills().Should().BeEmpty();
        }

        [Fact]
        public void PastaQuebrada_NaoDerrubaAsOutras()
        {
            // Uma skill malformada não pode impedir as outras de existirem — e muito menos
            // derrubar a montagem do prompt de sistema, que é onde a lista é usada.
            Instalar("quebrada", "isto não tem cabeçalho nenhum");
            Instalar("boa", "---\nname: boa\ndescription: funciona\ninterpreter: markdown\n---\ncorpo");

            SkillService.ListLocalSkills().Select(s => s.Name).Should().Equal("boa");
        }

        [Fact]
        public void ScriptDeclaradoQueNaoExiste_DeixaOCaminhoVazio()
        {
            // A skill continua listada: ela pode ser de documentação, ou o usuário pode estar
            // no meio da instalação. Quem executa é que decide o que fazer com isso.
            Instalar("torta",
                "---\nname: torta\ndescription: script sumido\ninterpreter: powershell\nscript_file: nao-existe.ps1\n---\n");

            var skill = SkillService.Find("torta");

            skill.Should().NotBeNull();
            skill!.ScriptPath.Should().BeEmpty();
        }

        [Fact]
        public void Find_NaoDiferenciaMaiusculas()
        {
            Instalar("x", "---\nname: Ler-Planilha\ndescription: d\ninterpreter: markdown\n---\n");

            SkillService.Find("ler-planilha").Should().NotBeNull();
            SkillService.Find("LER-PLANILHA").Should().NotBeNull();
            SkillService.Find("outra").Should().BeNull();
        }

        [Fact]
        public void AspasNoCabecalho_SaemDoValor()
        {
            Instalar("y", "---\nname: \"com-aspas\"\ndescription: 'entre plicas'\ninterpreter: markdown\n---\n");

            var skill = SkillService.Find("com-aspas");

            skill.Should().NotBeNull();
            skill!.Description.Should().Be("entre plicas");
        }

        [Fact]
        public void RaizInexistente_DevolveListaVazia()
        {
            SkillService.SkillsDirectoryOverride = Path.Combine(_raiz, "nao-existe");

            SkillService.ListLocalSkills().Should().BeEmpty();
            SkillService.GetSkillCount().Should().Be(0);
        }
    }
}
