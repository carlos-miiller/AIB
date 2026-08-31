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
        // Formatacao de TEXTO nao e formatacao de DISCO. O padrao antigo era "\bformat\b", e o
        // hifen e caractere nao-palavra: havia fronteira logo depois de "format", entao a
        // palavra casava dentro do nome do cmdlet.
        //
        // Aconteceu em uso real: uma busca por um nome numa planilha foi recusada com
        // "formatacao/particao de disco", e o modelo passou os turnos seguintes tentando
        // contornar uma permissao que nunca esteve em jogo. Recusa que mente sobre o motivo
        // manda o agente procurar solucao no lugar errado.
        [InlineData(@"Import-Csv arquivo.csv | Format-Table")]
        [InlineData(@"Get-Process | Format-List")]
        [InlineData(@"Get-Content bin | Format-Hex")]
        [InlineData(@"Get-ChildItem | Format-Table -AutoSize")]
        public void FormatacaoDeTexto_NaoEBloqueada(string comando)
        {
            var (hit, _) = CommandFloorList.Match(comando, userLevel: 1);

            hit.Should().BeFalse($"'{comando}' formata SAIDA, nao disco");
        }

        [Theory]
        // A contrapartida: os cmdlets de disco que de fato destroem. Clear-Disk,
        // Initialize-Disk e as operacoes de particao nao eram alcancadas pelo padrao antigo.
        [InlineData(@"Format-Volume -DriveLetter D")]
        [InlineData(@"Clear-Disk -Number 1 -RemoveData")]
        [InlineData(@"Initialize-Disk -Number 2")]
        [InlineData(@"New-Partition -DiskNumber 1 -UseMaximumSize")]
        [InlineData(@"Remove-Partition -DiskNumber 1 -PartitionNumber 2")]
        [InlineData(@"format D: /fs:ntfs")]
        public void FormatacaoDeDisco_ContinuaBloqueada(string comando)
        {
            var (hit, reason) = CommandFloorList.Match(comando, userLevel: 6);

            hit.Should().BeTrue($"'{comando}' mexe em disco");
            reason.Should().Contain("ACESSO NEGADO (FLOOR)");
        }

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
