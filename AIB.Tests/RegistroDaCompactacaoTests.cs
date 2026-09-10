using System;
using System.IO;
using System.Linq;
using AIB.Services.Memory;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O diário da compactação.
    /// <para>
    /// A pasta da sessão já guardava o QUE a compactação produziu — <c>raw.jsonl</c>,
    /// <c>chapters.jsonl</c>, <c>acts.jsonl</c>. O que não ficava em lugar nenhum era o CUSTO, e
    /// principalmente as vezes em que ela FALHOU: um resumo que estoura o teto de quatro minutos
    /// imprimia uma linha no console e morria ali, e na execução seguinte ninguém sabia que a
    /// conversa tinha andado com a poda de emergência.
    /// </para>
    /// </summary>
    public class RegistroDaCompactacaoTests : IDisposable
    {
        private readonly string _dir;

        public RegistroDaCompactacaoTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aib-diario-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private RegistroDaCompactacao Novo(bool ligado = true) =>
            new(() => ligado, () => _dir);

        private string Conteudo() =>
            File.Exists(Path.Combine(_dir, RegistroDaCompactacao.NomeDoArquivo))
                ? File.ReadAllText(Path.Combine(_dir, RegistroDaCompactacao.NomeDoArquivo))
                : "";

        // ─────────────────────────────────────────────────────────────────────
        // A chave
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void DESLIGADO_NaoCriaArquivoNenhum()
        {
            var registro = Novo(ligado: false);

            registro.Gatilho(9000, 8000, 10000, 5);
            registro.Falhou("compactacao", "qualquer coisa");

            Directory.GetFiles(_dir).Should().BeEmpty("desligado é desligado, não 'grava vazio'");
        }

        [Fact]
        public void AChave_EhLIDA_ACadaEscrita()
        {
            // A tela pode ligar a chave no meio da conversa, e o diário precisa passar a gravar
            // sem reiniciar o programa.
            bool ligado = false;
            var registro = new RegistroDaCompactacao(() => ligado, () => _dir);

            registro.Gatilho(1, 2, 3, 4);
            Conteudo().Should().BeEmpty();

            ligado = true;
            registro.Gatilho(9000, 8000, 10000, 5);
            Conteudo().Should().Contain("GATILHO");
        }

        [Fact]
        public void APasta_EhRESOLVIDA_ACadaEscrita()
        {
            // _sessionMemory troca quando o usuário zera a conversa. Um caminho congelado na
            // construção escreveria o diário da conversa NOVA dentro da pasta da ANTIGA.
            string segunda = Path.Combine(_dir, "sessao-2");
            string atual = _dir;

            var registro = new RegistroDaCompactacao(() => true, () => atual);
            registro.Gatilho(9000, 8000, 10000, 5);

            atual = segunda;
            registro.Gatilho(9000, 8000, 10000, 5);

            File.Exists(Path.Combine(segunda, RegistroDaCompactacao.NomeDoArquivo))
                .Should().BeTrue();
        }

        // ─────────────────────────────────────────────────────────────────────
        // O que ele grava
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OArquivo_MORA_NaPastaDaSessao_JuntoDoRaw()
        {
            // Um registro de compactação separado da conversa que ele compactou obriga a cruzar
            // horário à mão.
            Novo().Gatilho(9412, 8700, 10235, 5);

            Path.GetFileName(Novo().Caminho).Should().Be("compactacao.log");
            File.Exists(Path.Combine(_dir, "compactacao.log")).Should().BeTrue();
        }

        [Fact]
        public void OCabecalho_NASCE_UmaVezSo()
        {
            var registro = Novo();

            registro.Gatilho(9412, 8700, 10235, 5);
            registro.Capitulo(3, 5, 12, 16);
            registro.CapituloFechado(9933, 4, 3480);

            Conteudo().Split("diário da compactação").Length.Should().Be(2, "o cabeçalho não se repete");
        }

        [Fact]
        public void OGatilho_TRAZ_AsTresGrandezas()
        {
            // Sozinho, "compactando 5 turnos" não diz se foi cedo ou tarde demais.
            Novo().Gatilho(9412, 8700, 10235, 5);

            string texto = Conteudo();
            texto.Should().Contain("9.412");
            texto.Should().Contain("8.700");
            texto.Should().Contain("10.235");
            texto.Should().Contain("5 turno(s)");
        }

        [Fact]
        public void OResumo_TRAZ_TempoPrefillESaida()
        {
            // Foi essa medição que mostrou que o raciocínio custava 226,7s dos 286,6s para
            // produzir 117 tokens de texto.
            Novo().Resumo(14700, 3204, 117, 96);

            string texto = Conteudo();
            texto.Should().Contain("14,7s");
            texto.Should().Contain("3.204 tok");
            texto.Should().Contain("117 tok");
            texto.Should().Contain("96 palavra(s)");
        }

        [Fact]
        public void ContagemAUSENTE_VIRA_NaoDisponivel_NaoZero()
        {
            // O OpenAI não devolve os mesmos campos do Ollama, e escrever "0" onde o número não
            // existe inventaria uma medição.
            Novo().Resumo(1000, null, null, 10);

            Conteudo().Should().Contain("n/d");
            Conteudo().Should().NotContain("0 tok");
        }

        [Fact]
        public void AFalha_EhAMaisImportante_EAparece()
        {
            Novo().Falhou("compactacao", "cancelada no teto de 4 min");

            string texto = Conteudo();
            texto.Should().Contain("FALHOU");
            texto.Should().Contain("cancelada no teto de 4 min");
        }

        [Fact]
        public void PULOU_TemCausa_EAcausaEhEscrita()
        {
            // "Passou do gatilho e não compactou" tem causa, e sem esta linha o diário mostraria
            // um silêncio inexplicável.
            Novo().Pulou("vivo=9000 > limite=8000, mas nenhum turno elegivel");

            Conteudo().Should().Contain("PULOU").And.Contain("nenhum turno elegivel");
        }

        [Fact]
        public void OCapituloEOAto_APARECEM_ComOsIndices()
        {
            var registro = Novo();

            registro.Capitulo(3, 5, 12, 16);
            registro.CapituloFechado(9933, 4, 3480);
            registro.Ato(0, 4, 0, 3);
            registro.AtoFechado(3, 2);

            string texto = Conteudo();
            texto.Should().Contain("CAPÍTULO 3").And.Contain("turnos 12–16");
            texto.Should().Contain("9.933 token(s) saíram do prompt");
            texto.Should().Contain("ATO 0").And.Contain("capítulos 0–3");
            texto.Should().Contain("2 fato(s)");
        }

        [Fact]
        public void ELE_ACRESCENTA_NaoSobrescreve()
        {
            // Append-only, como o raw.jsonl ao lado. Uma execução nova não pode apagar o
            // diagnóstico da anterior — é justamente a anterior que se quer investigar.
            Novo().Gatilho(1000, 900, 1200, 1);
            Novo().Gatilho(2000, 900, 1200, 2);

            Conteudo().Split("GATILHO").Length.Should().Be(3);
        }

        [Fact]
        public void PastaIMPOSSIVEL_NaoDerrubaACompactacao()
        {
            // Diagnóstico que derruba a compactação seria pior que a falta dele.
            var registro = new RegistroDaCompactacao(() => true, () => "\0caminho\0invalido");

            Action escrever = () => registro.Gatilho(1, 2, 3, 4);

            escrever.Should().NotThrow();
        }

        [Fact]
        public void AsPalavrasDoResumo_SaoContadas()
        {
            // O pedido é "no máximo 120 palavras", e é a contagem que diz se o modelo obedeceu —
            // o número de tokens não responde isso.
            Compactor.Palavras("um dois três").Should().Be(3);
            Compactor.Palavras("  espaços   demais  ").Should().Be(2);
            Compactor.Palavras("").Should().Be(0);
            Compactor.Palavras(null).Should().Be(0);
        }
    }
}
