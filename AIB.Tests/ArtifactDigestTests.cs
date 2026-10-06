using System.Collections.Generic;
using System.Linq;
using AIB.Services.Memory;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Condensação e destilação de artefatos. Tudo aqui é função pura — nenhum modelo é
    /// chamado, e é essa a razão de existirem: o literal não pode depender de o resumidor
    /// acertar.
    /// </summary>
    public class ArtifactDigestTests
    {
        private static Artifact Arquivo(string caminho, bool falhou = false) =>
            new(ArtifactKind.FileRead, "read", caminho, falhou);

        private static Artifact Gravou(string caminho) =>
            new(ArtifactKind.FileWritten, "write", caminho, false);

        private static Artifact Comando(string linha, bool falhou = false) =>
            new(ArtifactKind.CommandRun, "shell", linha, falhou);

        private static Chapter Capitulo(int indice, params Artifact[] artefatos) =>
            new(indice, "2026-08-24T00:00:00Z", indice * 2, indice * 2 + 1, $"resumo {indice}", artefatos);

        // ─────────────────────────────────────────────────────────────────────
        // KeyOf
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void LerEGravarOMesmoArquivo_SaoOMesmoFato()
        {
            // O que importa é o caminho ser recorrente, não por qual porta ele passou.
            ArtifactDigest.KeyOf(Arquivo(@"C:\x\a.cs"))
                .Should().Be(ArtifactDigest.KeyOf(Gravou(@"C:\x\a.cs")));
        }

        [Fact]
        public void ComandoEArquivoDeMesmoTexto_NaoSeMisturam()
        {
            ArtifactDigest.KeyOf(Comando("dir"))
                .Should().NotBe(ArtifactDigest.KeyOf(Arquivo("dir")));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Condense
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Condense_ColapsaRepetidosEmUmSo()
        {
            var resultado = ArtifactDigest.Condense(new[]
            {
                Arquivo(@"C:\x\a.cs"), Gravou(@"C:\x\a.cs"), Arquivo(@"C:\x\a.cs")
            });

            resultado.Should().ContainSingle();
            resultado[0].Value.Should().Be(@"C:\x\a.cs");
        }

        [Fact]
        public void Condense_PoeOQueFalhouNaFrente()
        {
            // Um caminho lido com sucesso o agente reencontra; um comando que quebrou, se
            // esquecido, ele repete.
            var resultado = ArtifactDigest.Condense(new[]
            {
                Arquivo(@"C:\ok1.cs"), Arquivo(@"C:\ok2.cs"), Comando("git push", falhou: true)
            });

            resultado[0].Value.Should().Be("git push");
        }

        [Fact]
        public void Condense_RespeitaOTeto()
        {
            var muitos = Enumerable.Range(0, 40).Select(i => Arquivo($@"C:\f{i}.cs")).ToList();

            ArtifactDigest.Condense(muitos, max: 5).Should().HaveCount(5);
        }

        [Fact]
        public void Condense_PrefereORepresentanteComDetalhe()
        {
            var semDetalhe = new Artifact(ArtifactKind.FileWritten, "write", @"C:\a.txt", false);
            var comDetalhe = new Artifact(ArtifactKind.FileWritten, "write", @"C:\a.txt", false, "1,2 KB");

            ArtifactDigest.Condense(new[] { semDetalhe, comDetalhe })[0].Detail.Should().Be("1,2 KB");
        }

        [Fact]
        public void Condense_IgnoraValorVazio()
        {
            ArtifactDigest.Condense(new[] { Arquivo(""), Arquivo("   ") }).Should().BeEmpty();
        }

        [Fact]
        public void Condense_ToleraNulo()
        {
            ArtifactDigest.Condense(null).Should().BeEmpty();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Distill
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Distill_PromoveOQueAtravessouCapitulosSuficientes()
        {
            var fatos = ArtifactDigest.Distill(new[]
            {
                Capitulo(0, Arquivo(@"C:\quente.cs")),
                Capitulo(1, Arquivo(@"C:\quente.cs")),
                Capitulo(2, Arquivo(@"C:\quente.cs"))
            });

            fatos.Should().ContainSingle();
            fatos[0].Line.Should().Contain(@"C:\quente.cs");
        }

        [Fact]
        public void Distill_ComandoQueApaga_ViraFatoSemALinhaDeComando()
        {
            // O fato vai ao prompt de toda conversa. Um Remove-Item recorrente virava "comando
            // usado neste ambiente", pronto para o modelo copiar e apagar de novo.
            const string apaga = @"Remove-Item ""C:\trabalho\saida"" -Recurse -Force";
            var fatos = ArtifactDigest.Distill(new[]
            {
                Capitulo(0, Comando(apaga)), Capitulo(1, Comando(apaga)), Capitulo(2, Comando(apaga))
            });

            fatos.Should().ContainSingle().Which.Line
                .Should().NotContain("Remove-Item").And.Contain(@"C:\trabalho\saida").And.Contain("não repetir");
        }

        [Fact]
        public void ParaOPrompt_DescreveOFatoAntigoComComandoQueApaga_ENaoTocaOQueOUsuarioEscreveu()
        {
            // facts.md gravado antes da regra ainda tem a linha literal.
            ArtifactDigest.ParaOPrompt(@"- comando usado neste ambiente: rm -rf C:\x (recorrente em 3 capítulos)")
                .Should().NotContain("rm -rf");

            ArtifactDigest.ParaOPrompt("- nunca rode rm -rf nesta máquina")
                .Should().Be("- nunca rode rm -rf nesta máquina", "linha escrita à mão fica como está");

            ArtifactDigest.ParaOPrompt("- comando usado neste ambiente: dotnet test")
                .Should().Be("- comando usado neste ambiente: dotnet test");
        }

        [Fact]
        public void Distill_NaoPromoveOQueApareceuPouco()
        {
            var fatos = ArtifactDigest.Distill(new[]
            {
                Capitulo(0, Arquivo(@"C:\morno.cs")),
                Capitulo(1, Arquivo(@"C:\morno.cs"))
            });

            fatos.Should().BeEmpty("duas ocorrências ainda são coincidência plausível");
        }

        [Fact]
        public void Distill_ContaCapitulosDistintosENaoOcorrencias()
        {
            // Gravar o mesmo arquivo cinco vezes dentro de um capítulo é um trabalho só.
            var fatos = ArtifactDigest.Distill(new[]
            {
                Capitulo(0, Arquivo(@"C:\a.cs"), Arquivo(@"C:\a.cs"), Arquivo(@"C:\a.cs"),
                            Arquivo(@"C:\a.cs"), Arquivo(@"C:\a.cs"))
            });

            fatos.Should().BeEmpty();
        }

        [Fact]
        public void Distill_PromoveRecusaNaPrimeiraOcorrencia()
        {
            // Recusa é decisão do usuário, e decisão não precisa se repetir para valer.
            var fatos = ArtifactDigest.Distill(new[]
            {
                Capitulo(0, new Artifact(ArtifactKind.Denied, "shell", "format C:", false))
            });

            fatos.Should().ContainSingle();
            fatos[0].Line.Should().Contain("NEGOU");
            fatos[0].Line.Should().Contain("format C:");
        }

        [Fact]
        public void Distill_MarcaComandoQueJaFalhou()
        {
            var fatos = ArtifactDigest.Distill(new[]
            {
                Capitulo(0, Comando("npm test", falhou: true)),
                Capitulo(1, Comando("npm test")),
                Capitulo(2, Comando("npm test"))
            });

            fatos.Should().ContainSingle();
            fatos[0].Line.Should().Contain("já falhou");
        }

        [Fact]
        public void Distill_DevolveOrdemEstavel()
        {
            // Sem ordem estável o facts.md sairia diferente a cada execução e ficaria
            // impossível de comparar.
            var capitulos = new List<Chapter>
            {
                Capitulo(0, Arquivo(@"C:\b.cs"), Arquivo(@"C:\a.cs")),
                Capitulo(1, Arquivo(@"C:\a.cs"), Arquivo(@"C:\b.cs")),
                Capitulo(2, Arquivo(@"C:\b.cs"), Arquivo(@"C:\a.cs"))
            };

            var primeira = ArtifactDigest.Distill(capitulos).Select(f => f.Key);
            var segunda = ArtifactDigest.Distill(capitulos).Select(f => f.Key);

            primeira.Should().Equal(segunda);
            primeira.Should().BeInAscendingOrder();
        }

        [Fact]
        public void Distill_ToleraNulo()
        {
            ArtifactDigest.Distill(null).Should().BeEmpty();
        }

        [Fact]
        public void Distill_ChaveNaoMudaEntreExecucoes()
        {
            // A chave é o que impede o mesmo fato de ser gravado duas vezes. Se ela variasse,
            // o facts.md encheria de duplicatas.
            var fatos = ArtifactDigest.Distill(new[]
            {
                Capitulo(0, Arquivo(@"C:\a.cs")),
                Capitulo(1, Gravou(@"C:\a.cs")),
                Capitulo(2, Arquivo(@"C:\a.cs"))
            });

            fatos.Should().ContainSingle();
            fatos[0].Key.Should().Be(@"arquivo|C:\a.cs");
        }
    }
}
