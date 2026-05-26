using System.Collections.Generic;

namespace AIB.Services;

/// <summary>
/// Contém o código-fonte das skills padrão que vêm embutidas na AIB.
/// </summary>
public static class DefaultSkills
{
    public static readonly List<SkillMetadata> Skills = new()
    {
        new SkillMetadata
        {
            Name = "consultar_cep",
            Description = "Busca informações de endereço a partir de um CEP no Brasil via ViaCEP.",
            Interpreter = "python",
            ScriptFile = "main.py"
        },
        new SkillMetadata
        {
            Name = "system_info",
            Description = "Retorna um resumo do sistema (Uso de RAM, CPU e Uptime).",
            Interpreter = "powershell",
            ScriptFile = "main.ps1"
        }
    };

    public static string GetScriptContent(string skillName)
    {
        return skillName switch
        {
            "consultar_cep" => """
import sys
import json
import urllib.request

def consultar_cep(cep):
    cep = ''.join(filter(str.isdigit, cep))
    if len(cep) != 8:
        print("Erro: CEP inválido. Deve conter 8 dígitos.")
        return

    url = f"https://viacep.com.br/ws/{cep}/json/"
    try:
        req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})
        with urllib.request.urlopen(req) as response:
            data = json.loads(response.read().decode())
            if "erro" in data:
                print("Erro: CEP não encontrado.")
            else:
                print(f"Endereço: {data.get('logradouro')}, Bairro: {data.get('bairro')}")
                print(f"Cidade/UF: {data.get('localidade')}/{data.get('uf')}")
    except Exception as e:
        print(f"Erro ao consultar ViaCEP: {e}")

if __name__ == "__main__":
    if len(sys.argv) > 1:
        consultar_cep(sys.argv[1])
    else:
        print("Uso: python main.py <cep>")
""",
            "system_info" => """
$os = Get-WmiObject Win32_OperatingSystem
$cpu = Get-WmiObject Win32_Processor
$totalRam = [math]::Round($os.TotalVisibleMemorySize / 1024)
$freeRam = [math]::Round($os.FreePhysicalMemory / 1024)
$usedRam = $totalRam - $freeRam

Write-Host "=== Status do Sistema ==="
Write-Host "SO: $($os.Caption)"
Write-Host "CPU: $($cpu.Name)"
Write-Host "RAM Total: $totalRam MB"
Write-Host "RAM Livre: $freeRam MB"
""",
            _ => ""
        };
    }
}
