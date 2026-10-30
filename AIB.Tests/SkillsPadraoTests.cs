using System;
using System.Diagnostics;
using System.IO;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// As skills que vêm com o AIB, espelhadas em <c>~/.AIB/skills/default</c>.
    /// <para>
    /// Caso real: a IA da Ellen reescreveu o <c>bitrix.ps1</c>, o script que roda com o segredo do
    /// Bitrix e que trava as tarefas no dono. Numa skill do AIB, a versão mexida não pode virar a
    /// versão em uso: o arranque devolve o original, e uma skill do usuário com o mesmo nome não
    /// toma o lugar (nem o segredo, que o cofre libera pelo nome).
    /// </para>
    /// </summary>
    [Collection("Skills")]
    public class SkillsPadraoTests : IDisposable
    {
        private readonly string? _anterior = SkillService.SkillsDirectoryOverride;
        private readonly string _base;
        private readonly string _origem;
        private readonly string _raiz;
        private string Destino => Path.Combine(_raiz, SkillsPadrao.Pasta);

        public SkillsPadraoTests()
        {
            _base = Path.Combine(Path.GetTempPath(), "aib-padrao-" + Guid.NewGuid().ToString("N"));
            _origem = Path.Combine(_base, "programa", "skills");
            _raiz = Path.Combine(_base, "dados", "skills");
            Directory.CreateDirectory(_origem);
            Directory.CreateDirectory(_raiz);
            SkillService.SkillsDirectoryOverride = _raiz;
        }

        public void Dispose()
        {
            SkillService.SkillsDirectoryOverride = _anterior;
            try { Directory.Delete(_base, true); } catch { }
        }

        private static void Skill(string pasta, string nome, string descricao = "d", string? script = null)
        {
            Directory.CreateDirectory(pasta);
            File.WriteAllText(Path.Combine(pasta, "SKILL.md"),
                $"---\nname: {nome}\ndescription: {descricao}\ninterpreter: powershell\nscript_file: s.ps1\n---\n");
            if (script != null) File.WriteAllText(Path.Combine(pasta, "s.ps1"), script);
        }

        [Fact]
        public void ScriptMexido_VoltaAoOriginalNoArranque()
        {
            Skill(Path.Combine(_origem, "bitrix"), "bitrix", script: "# trava 83487");
            SkillsPadrao.Sincronizar(_origem, Destino);

            string copia = Path.Combine(Destino, "bitrix", "s.ps1");
            File.WriteAllText(copia, "# sem trava");

            SkillsPadrao.Sincronizar(_origem, Destino);

            File.ReadAllText(copia).Should().Be("# trava 83487");
        }

        [Fact]
        public void OQueNaoVemComOPrograma_SaiDoDefault()
        {
            Skill(Path.Combine(_origem, "oficial"), "oficial");
            Skill(Path.Combine(Destino, "plantada"), "plantada");
            File.WriteAllText(Path.Combine(Destino, "solto.txt"), "x");

            SkillsPadrao.Sincronizar(_origem, Destino);

            Directory.Exists(Path.Combine(Destino, "plantada")).Should().BeFalse();
            File.Exists(Path.Combine(Destino, "solto.txt")).Should().BeFalse();
            Directory.Exists(Path.Combine(Destino, "oficial")).Should().BeTrue();
        }

        [Fact]
        public void SemPastaDoPrograma_NaoApagaNada()
        {
            // Um AIB instalado sem a pasta skills/ não pode esvaziar o default de quem já tinha.
            Skill(Path.Combine(Destino, "ja-estava"), "ja-estava");

            SkillsPadrao.Sincronizar(Path.Combine(_base, "nao-existe"), Destino);
            SkillsPadrao.Sincronizar(null, Destino);

            Directory.Exists(Path.Combine(Destino, "ja-estava")).Should().BeTrue();
        }

        [Fact]
        public void DefaultVirouJuncao_OAlvoNaoEApagado()
        {
            // Se alguém trocar o default por uma junção para outra pasta, limpar o default
            // apagaria a pasta apontada. Sai o link; o alvo fica.
            string alvo = Path.Combine(_base, "documentos");
            Directory.CreateDirectory(alvo);
            File.WriteAllText(Path.Combine(alvo, "importante.txt"), "não apagar");
            var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Destino}\" \"{alvo}\"")
            { CreateNoWindow = true, UseShellExecute = false })!;
            mk.WaitForExit();
            Directory.Exists(Destino).Should().BeTrue("a junção precisa existir para o ensaio valer");

            Skill(Path.Combine(_origem, "oficial"), "oficial");
            SkillsPadrao.Sincronizar(_origem, Destino);

            File.Exists(Path.Combine(alvo, "importante.txt")).Should().BeTrue();
            new DirectoryInfo(Destino).Attributes.HasFlag(FileAttributes.ReparsePoint).Should().BeFalse();
            Directory.Exists(Path.Combine(Destino, "oficial")).Should().BeTrue();
        }

        [Fact]
        public void SkillsDoAibEDoUsuario_AparecemJuntas()
        {
            Skill(Path.Combine(Destino, "oficial"), "oficial");
            Skill(Path.Combine(_raiz, "minha"), "minha");

            SkillService.ListLocalSkills().Should().HaveCount(2);
            SkillService.Find("oficial")!.Folder.Should().StartWith(Destino);
        }

        [Fact]
        public void NomeRepetido_ValeADoAib()
        {
            Skill(Path.Combine(Destino, "bitrix"), "bitrix", descricao: "do AIB");
            Skill(Path.Combine(_raiz, "bitrix-copia"), "Bitrix", descricao: "imitação");

            var lista = SkillService.ListLocalSkills();

            lista.Should().ContainSingle();
            lista[0].Description.Should().Be("do AIB");
        }
    }
}
