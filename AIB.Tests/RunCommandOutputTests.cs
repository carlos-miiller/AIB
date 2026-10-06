using System.Reflection;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A limpeza da saída de erro do PowerShell.
    /// <para>
    /// Com stdout e stderr redirecionados, o PowerShell serializa em CLIXML tudo que não é
    /// texto e despeja no stderr. Um <c>Get-ChildItem -Recurse</c> devolvia meio kilobyte de
    /// <c>&lt;Obj S="progress"&gt;</c> junto com três linhas de resultado útil — e isso ia
    /// inteiro para o histórico e para o resumo do capítulo.
    /// </para>
    /// </summary>
    public class RunCommandOutputTests
    {
        private static string Limpar(string? stderr) =>
            (string)typeof(RunCommandTool)
                .GetMethod("SemClixml", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object?[] { stderr })!;

        private const string Ruido =
            "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
            + "<Obj S=\"progress\" RefId=\"0\"><MS><AV>Preparando módulos para primeiro uso.</AV></MS></Obj></Objs>";

        [Fact]
        public void BlocoDeProgresso_SaiInteiro()
        {
            Limpar(Ruido).Should().BeEmpty();
        }

        [Fact]
        public void ErroSerializadoDentroDoBloco_ChegaAoModelo()
        {
            // A primeira versão disto jogava o bloco inteiro fora, e com isso cegava o modelo:
            // um cmdlet que falhasse devolvia "sem saída", e ele seguia adiante achando que não
            // havia o que corrigir. O erro do PowerShell viaja DENTRO do CLIXML.
            string comErro =
                "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
                + "<S S=\"Error\">Import-Csv : Não é possível processar o arquivo.</S></Objs>";

            Limpar(comErro).Should().Contain("Import-Csv")
                .And.Contain("Não é possível processar o arquivo");
        }

        [Fact]
        public void ProgressoNaoContamina_OTextoDoErro()
        {
            string misturado =
                "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
                + "<Obj S=\"progress\" RefId=\"0\"><MS><AV>Preparando módulos para primeiro uso.</AV></MS></Obj>"
                + "<S S=\"Error\">Falha de verdade.</S></Objs>";

            string limpo = Limpar(misturado);

            limpo.Should().Contain("Falha de verdade");
            limpo.Should().NotContain("Preparando módulos");
        }

        [Fact]
        public void ErroDeVerdade_AntesDoBloco_EPreservado()
        {
            // Erro real e CLIXML saem pelo mesmo cano. Cortar do começo ao fim engoliria o erro
            // junto com o ruído, e o modelo ficaria cego para a falha que precisa corrigir.
            Limpar("Get-ChildItem: acesso negado ao caminho X.\r\n" + Ruido)
                .Should().Contain("acesso negado");
        }

        // ─────────────────────────────────────────────────────────────────────
        // A falha dita na primeira palavra
        // ─────────────────────────────────────────────────────────────────────

        private const string Cabeca =
            "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">";

        [Fact]
        public void CodigoDeSaidaDiferenteDeZero_ViraERRO_ComASaidaJunto()
        {
            // Caso real: powershell -File apontando para um script que a pasta apagada levou.
            string r = RunCommandTool.Montar(
                "O argumento 'C:\\x\\gerar.ps1' para o parâmetro -File não existe.", "", -196608);

            r.Should().StartWith("ERRO (código de saída -196608): O argumento");
            AIB.Services.Memory.ArtifactExtractor.Falhou(r).Should().BeTrue("é o que marca [FALHOU] na memória");
        }

        [Fact]
        public void ErroQueNaoEncerra_ComCodigoZero_TambemViraERRO()
        {
            // O script segue depois do Get-Content que falhou e sai com 0. Só o registro de erro
            // no CLIXML denuncia — e era esse o caso da pasta apagada, que o resumo explicou
            // como "caracteres especiais".
            string stderr = Cabeca
                + "<S S=\"Error\">Get-Content : Não é possível localizar o caminho 'C:\\x\\template.html'._x000D__x000A_</S>"
                + "<S S=\"Error\">No linha:1 caractere:1_x000D__x000A_</S>"
                + "<S S=\"Error\">    + FullyQualifiedErrorId : PathNotFound,Microsoft.PowerShell.Commands.GetContentCommand_x000D__x000A_</S>"
                + "</Objs>";

            string r = RunCommandTool.Montar("Processamento concluído.", stderr, 0);

            r.Should().StartWith("ERRO: o comando continuou, mas houve erro: Get-Content : Não é possível localizar");
            r.Should().Contain("Saída completa:").And.Contain("Processamento concluído.");
        }

        [Fact]
        public void ProgressoDeProgramaNativo_ComCodigoZero_NaoEhFalha()
        {
            // git escreve progresso em stderr; com 2>&1 o PowerShell 5.1 embrulha como erro.
            string stderr = Cabeca
                + "<S S=\"Error\">git : Cloning into 'repo'..._x000D__x000A_</S>"
                + "<S S=\"Error\">    + FullyQualifiedErrorId : NativeCommandError_x000D__x000A_</S>"
                + "</Objs>";

            RunCommandTool.Montar("", stderr, 0).Should().NotStartWith("ERRO");
        }

        [Fact]
        public void SaidaDoProgramaNoStderr_ComCodigoUm_NAO_EhFalha_MasAvisa()
        {
            // O caso da conversa do GLPI: o teste PHP imprimia "HOOK chamado" no stderr do
            // container, o PowerShell devolvia código 1, e o modelo leu ERRO num teste que
            // passou — foi consertar o que não estava quebrado.
            string stderr = Cabeca
                + "<S S=\"Error\">docker : HOOK chamado: Change status=6_x000D__x000A_</S>"
                + "<S S=\"Error\">    + FullyQualifiedErrorId : NativeCommandError_x000D__x000A_</S>"
                + "</Objs>";

            string r = RunCommandTool.Montar("LocA=9 LocB=10", stderr, 1);

            r.Should().NotStartWith("ERRO");
            r.Should().Contain("LocA=9 LocB=10").And.Contain("código 1");
        }

        [Fact]
        public void ErroDeVerdadeNoStderr_ComCodigoUm_CONTINUA_Falha()
        {
            // A marca é o que separa: "cannot open" é erro; a saída comum do programa, não.
            string stderr = Cabeca
                + "<S S=\"Error\">docker : head: cannot open '/var/www/glpi/index.php' for reading_x000D__x000A_</S>"
                + "<S S=\"Error\">    + FullyQualifiedErrorId : NativeCommandError_x000D__x000A_</S>"
                + "</Objs>";

            RunCommandTool.Montar("", stderr, 1).Should().StartWith("ERRO (código de saída 1)");
        }

        [Fact]
        public void CodigoDeSaidaSemRegistroDeErro_CONTINUA_Falha()
        {
            // Sem NativeCommandError não há ruído de embrulho a descontar: o código de saída é o
            // único sinal que sobrou, e ele diz que falhou.
            RunCommandTool.Montar("nada feito", "", 2).Should().StartWith("ERRO (código de saída 2)");
        }

        [Fact]
        public void StderrEmTextoPuro_ComCodigoZero_NaoEhFalha()
        {
            RunCommandTool.Montar("ok", "npm WARN deprecated algo", 0).Should().NotStartWith("ERRO");
        }

        [Fact]
        public void Sucesso_SaiComoAntes()
        {
            RunCommandTool.Montar("linha 1\n", "", 0).Should().Be("linha 1");
            RunCommandTool.Montar("", "", 0).Should().Be("Comando executado com sucesso (sem saída).");
            RunCommandTool.Montar("", Ruido, 0).Should().Be("Comando executado com sucesso (sem saída).");
        }

        [Fact]
        public void DoisErros_OPrimeiroVaiNaCabeca()
        {
            string stderr = Cabeca
                + "<S S=\"Error\">Primeiro erro._x000D__x000A_</S>"
                + "<S S=\"Error\">    + FullyQualifiedErrorId : A_x000D__x000A_</S>"
                + "<S S=\"Error\">Segundo erro._x000D__x000A_</S>"
                + "<S S=\"Error\">    + FullyQualifiedErrorId : B_x000D__x000A_</S>"
                + "</Objs>";

            RunCommandTool.Montar("", stderr, 1).Should().StartWith("ERRO (código de saída 1): Primeiro erro.")
                .And.Contain("Segundo erro.");
        }

        [Fact]
        public void SemBloco_NadaMuda()
        {
            Limpar("erro comum").Should().Be("erro comum");
            Limpar("").Should().BeEmpty();
            Limpar(null).Should().BeEmpty();
        }
    }
}
