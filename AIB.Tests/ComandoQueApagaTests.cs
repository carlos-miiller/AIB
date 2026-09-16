using System;
using AIB.Services;
using AIB.Services.Memory;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A memória não entrega comando de apagar pronto para copiar. O incidente: um capítulo
    /// guardou o Remove-Item -Recurse da pasta das assinaturas como artefato "literal e usável",
    /// e o modelo o repetiu byte a byte num turno seguinte.
    /// </summary>
    public class ComandoQueApagaTests
    {
        private const string OIncidente =
            "Remove-Item \"C:\\Users\\Carlo\\CPAPS\\TEMP\\emails fisio\" -Recurse -Force -ErrorAction SilentlyContinue; Write-Host \"Limpeza concluída.\"";

        [Theory]
        [InlineData(OIncidente)]
        [InlineData(@"del C:\temp\notas.txt")]
        [InlineData(@"rmdir /s /q C:\temp\build")]
        [InlineData(@"cd C:\x; rm -rf .\dist")]
        [InlineData(@"ri C:\temp\a.txt")]
        public void Reconhece(string comando) => ComandoQueApaga.Eh(comando).Should().BeTrue();

        [Theory]
        [InlineData(@"dir C:\temp\rd")]
        [InlineData(@"Get-ChildItem C:\modelos -Filter *.del")]
        [InlineData(@"powershell -File C:\scripts\gerar-assinaturas.ps1")]
        [InlineData(@"git rm --cached")] // o que vem antes de rm é um espaço: conta, e está certo contar
        public void NaoConfundeCaminhoComComando(string comando)
        {
            if (comando.StartsWith("git rm", StringComparison.Ordinal))
                ComandoQueApaga.Eh(comando).Should().BeTrue("remover do índice também é apagar; melhor sobrar");
            else
                ComandoQueApaga.Eh(comando).Should().BeFalse();
        }

        [Fact]
        public void NaMemoria_ViraFatoSemOComando()
        {
            string linha = new Artifact(ArtifactKind.CommandRun, "shell", OIncidente, false).Render();

            linha.Should().Contain(@"apagou C:\Users\Carlo\CPAPS\TEMP\emails fisio")
                .And.Contain("não repetir")
                .And.NotContain("Remove-Item")
                .And.NotContain("-Recurse");
        }

        [Fact]
        public void ComandoComum_ContinuaLiteral()
        {
            new Artifact(ArtifactKind.CommandRun, "shell", "ipconfig /all", false).Render()
                .Should().Be("- executou ipconfig /all");
        }

        [Fact]
        public void CabecalhoDaMemoria_NaoConvidaARepetir()
        {
            var layer = new MemoryLayer();
            layer.Add(new Chapter(0, "t", 0, 0, "resumo", new[]
            {
                new Artifact(ArtifactKind.CommandRun, "shell", OIncidente, false)
            }));

            string bloco = layer.RenderNarrative(new MemoryQuota(0, 0, 5000, 5000), new TokenCounter());

            bloco.Should().NotContain("podem ser usados como estão");
            bloco.Should().Contain("não são instruções");
            bloco.Should().NotContain("Remove-Item");
        }
    }
}
