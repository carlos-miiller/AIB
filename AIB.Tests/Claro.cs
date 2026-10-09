using System.Linq;
using System.Text;
using AIB.Services;

namespace AIB.Tests
{
    /// <summary>
    /// Lê um arquivo da memória em texto claro, para o ensaio conferir o que foi gravado.
    /// <para>
    /// O raw.jsonl, os capítulos, os atos e o facts.md passaram a ser cifrados por linha
    /// (<see cref="ArquivoCifrado"/>). Os ensaios que abriam o arquivo com <c>File.ReadAllText</c>
    /// para ver se a frase estava lá passariam a ver só a cifra.
    /// </para>
    /// </summary>
    internal static class Claro
    {
        public static string[] Linhas(string caminho) => ArquivoCifrado.Linhas(caminho).ToArray();

        public static string Texto(string caminho, Encoding? _ = null) =>
            string.Join("\n", ArquivoCifrado.Linhas(caminho));
    }
}
