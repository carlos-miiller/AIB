using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O filtro de segredos na saída das ferramentas.
    /// <para>
    /// Caso real: um <c>docker inspect</c> devolveu as variáveis de ambiente de um contêiner, com
    /// a senha do banco dentro da string de conexão e o segredo do JWT. Os dois foram ao provedor
    /// do modelo e ficaram no <c>raw.jsonl</c> em texto claro. Os valores abaixo são inventados.
    /// </para>
    /// </summary>
    public class SegredosTests
    {
        [Fact]
        public void VariaveisDeAmbienteDoConteiner_SaemSemOsValores()
        {
            string saida =
                "\"Env\": [\n"
                + "  \"ASPNETCORE_ENVIRONMENT=Production\",\n"
                + "  \"ConnectionStrings__DefaultConnection=Host=db;Port=5432;Database=forms;Username=app;Password=Xy7!kLm29q\",\n"
                + "  \"Auth__JwtSecret=umsegredobemcompridodeverdade\",\n"
                + "  \"PATH=/usr/local/bin\"\n"
                + "]";

            string limpo = Segredos.Redigir(saida, out int quantos);

            limpo.Should().NotContain("Xy7!kLm29q").And.NotContain("umsegredobemcompridodeverdade");
            limpo.Should().Contain("Auth__JwtSecret=" + Segredos.Omitido, "o nome fica, para o modelo entender o que leu");
            limpo.Should().Contain("Password=" + Segredos.Omitido);
            limpo.Should().Contain("ASPNETCORE_ENVIRONMENT=Production").And.Contain("PATH=/usr/local/bin");
            quantos.Should().BeGreaterThan(1);
        }

        [Theory]
        [InlineData("DB_PASSWORD=hunter2abc", "hunter2abc")]
        [InlineData("export GITHUB_TOKEN=abcdef123456", "abcdef123456")]
        [InlineData("$env:OPENROUTER_API_KEY=valorqualquer99", "valorqualquer99")]
        [InlineData("\"password\": \"s3nh4-secreta\"", "s3nh4-secreta")]
        [InlineData("senha = 'minhasenha'", "minhasenha")]
        [InlineData("password: Tr0ub4dor&3", "Tr0ub4dor&3")]
        [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwx", "abcdefghijklmnopqrstuvwx")]
        [InlineData("postgres://app:s3nhaDoBanco@db:5432/forms", "s3nhaDoBanco")]
        [InlineData("https://site.com/cb?access_token=abc123def456&x=1", "abc123def456")]
        [InlineData("chave sk-or-v1-0123456789abcdef0123456789abcdef aqui", "sk-or-v1-0123456789abcdef0123456789abcdef")]
        [InlineData("ghp_0123456789abcdefghijABCDEFGHIJ012345", "ghp_0123456789abcdefghijABCDEFGHIJ012345")]
        [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", "eyJhbGciOiJIUzI1NiJ9")]
        public void OQueTemCaraDeSegredo_Sai(string texto, string valor)
        {
            string limpo = Segredos.Redigir(texto, out int quantos);

            limpo.Should().NotContain(valor);
            limpo.Should().Contain(Segredos.Omitido);
            quantos.Should().BeGreaterThan(0);
        }

        [Fact]
        public void ChavePrivada_SaiInteira()
        {
            string texto = "antes\n-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\nAAAAB3NzaC1\n-----END OPENSSH PRIVATE KEY-----\ndepois";

            string limpo = Segredos.Redigir(texto);

            limpo.Should().Be("antes\n" + Segredos.Omitido + "\ndepois");
        }

        [Theory]
        // Código-fonte é a leitura mais comum do AIB. Redigir isto estragaria a leitura sem
        // esconder segredo nenhum.
        [InlineData("conn = connect(user=u, password=self.password)")]
        [InlineData("var r = await Login(token=ct, senha=LerSenha())")]
        [InlineData("string password = txtPassword.Text;")]
        [InlineData("password: string;")]
        [InlineData("\"max_tokens\": 4096, \"TokenCount\": 12")]
        [InlineData("max_tokens=4096")]
        [InlineData("<input type=password name=senha>")]
        [InlineData("http://localhost:8080/painel")]
        [InlineData("    42\tO total é 38, não 42.")]
        public void OQueNaoEhSegredo_Fica(string texto)
        {
            Segredos.Redigir(texto, out int quantos).Should().Be(texto);
            quantos.Should().Be(0);
        }

        [Fact]
        public void RedigirDuasVezes_NaoMudaNada()
        {
            string uma = Segredos.Redigir("DB_PASSWORD=hunter2abc e \"token\": \"abcd1234\"");

            Segredos.Redigir(uma, out int quantos).Should().Be(uma);
            quantos.Should().Be(0);
        }

        [Fact]
        public async Task NoRegistry_OResultadoChegaLimpo_EComOAviso()
        {
            // É o ponto que protege o raw.jsonl: ele grava o que o modelo recebeu.
            var registry = new ToolRegistry();
            registry.Registrar(new FerramentaQueVaza());

            string resultado = await registry.ExecuteToolAsync("vaza", "{}", userLevel: 10);

            resultado.Should().NotContain("hunter2abc");
            resultado.Should().Contain("DB_PASSWORD=" + Segredos.Omitido);
            resultado.Should().Contain("omitido(s) pelo AIB", "sem o aviso o modelo acha que o comando falhou e tenta de novo");
        }

        [Fact]
        public async Task NoRegistry_ResultadoSemSegredo_VoltaIntacto()
        {
            var registry = new ToolRegistry();
            registry.Registrar(new FerramentaQueVaza("tudo certo"));

            (await registry.ExecuteToolAsync("vaza", "{}", userLevel: 10)).Should().Be("tudo certo");
        }

        [Fact]
        public void GravarAMarca_EhRecusado()
        {
            // O modelo lê um .env com a senha omitida e devolve o arquivo inteiro: a marca seria
            // gravada por cima da senha de verdade.
            string conteudo = "DB_HOST=db\\nDB_PASSWORD=" + Segredos.Omitido;

            new WriteFileTool().Validar($"{{\"path\":\"C:/tmp/aib-x/.env\",\"content\":\"{conteudo}\"}}")
                .Should().Be(Segredos.RecadoDeGravacao);
        }

        private sealed class FerramentaQueVaza : ITool
        {
            private readonly string _saida;
            public FerramentaQueVaza(string saida = "PATH=/bin\nDB_PASSWORD=hunter2abc") => _saida = saida;

            public string Name => "vaza";
            public string Description => "";
            public OpenAI.Chat.ChatTool ChatToolDefinition => OpenAI.Chat.ChatTool.CreateFunctionTool("vaza");
            public int RequiredLevel => 1;
            public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1) => Task.FromResult(_saida);
        }
    }
}
