using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Floor list de comandos destrutivos. Roda DEPOIS do modal e só refuta abaixo do Nível 7 —
    /// é rede best-effort, não o portão canônico.
    /// </summary>
    public class CommandFloorListTests
    {
        [Theory]
        // A forma nativa do PowerShell: era a que passava batido. O padrão original usava
        // "\b-recurse\b", e \b antes de hífen nunca casa — entre um espaço e um '-' os dois
        // lados são não-palavra, então não existe boundary. A entrada jamais disparou.
        [InlineData(@"Remove-Item -Recurse C:\dados")]
        [InlineData(@"remove-item -recurse c:\dados")]
        [InlineData(@"Remove-Item -Force -Recurse $HOME")]
        // Alias: 'ri' expande para remove-item antes do match.
        [InlineData(@"ri -Recurse C:\dados")]
        // Formas unix e cmd
        [InlineData(@"rm -rf /tmp")]
        [InlineData(@"rm -r algumapasta")]
        [InlineData(@"del /s C:\dados")]
        [InlineData(@"rmdir /s C:\dados")]
        // Outras categorias
        [InlineData(@"format C:")]
        [InlineData(@"diskpart")]
        [InlineData(@"shutdown /s /t 0")]
        [InlineData(@"Restart-Computer")]
        [InlineData(@"reg delete HKLM\Software\Algo /f")]
        [InlineData(@"Remove-Item -Path HKLM:\Software\Algo")]
        // Concatenação de string usada para escapar do matcher
        [InlineData(@"Remove" + "\"" + " + " + "\"" + @"-Item -Recurse C:\dados")]
        public void ComandosDestrutivos_SaoRefutadosAbaixoDoNivel7(string comando)
        {
            var (hit, reason) = CommandFloorList.Match(comando, userLevel: 6);

            hit.Should().BeTrue($"'{comando}' é destrutivo e o usuário está abaixo do Nível 7");
            reason.Should().StartWith("ACESSO NEGADO (FLOOR)");
        }

        [Theory]
        [InlineData(@"Get-Process")]
        [InlineData(@"Get-ChildItem C:\dados")]
        [InlineData(@"echo oi")]
        [InlineData(@"dotnet build")]
        // Menciona remove-item mas sem a flag destrutiva: apagar UM arquivo não é a categoria.
        [InlineData(@"Remove-Item C:\dados\arquivo.txt")]
        public void ComandosComuns_Passam(string comando)
        {
            CommandFloorList.Match(comando, userLevel: 1).Hit.Should().BeFalse();
        }

        [Fact]
        public void EncodedCommand_EhRecusadoPorNaoSerAvaliavel()
        {
            var (hit, reason) = CommandFloorList.Match(
                "powershell -EncodedCommand UwB0AG8AcAA=", userLevel: 6);

            hit.Should().BeTrue();
            reason.Should().Contain("EncodedCommand");
        }

        [Fact]
        public void NoNivel7_OFloorSaiDeCena_EOModalEhAAutoridadeUnica()
        {
            CommandFloorList.Match(@"Remove-Item -Recurse C:\dados", userLevel: 7)
                .Hit.Should().BeFalse();
        }

        [Fact]
        public void NoNivel7_NemMesmoEncodedCommandEhBarrado()
        {
            CommandFloorList.Match("powershell -EncodedCommand UwB0AG8AcAA=", userLevel: 7)
                .Hit.Should().BeFalse();
        }
    }
}
