# OtimizadorPC

Aplicativo para Windows 10/11 que reúne em um só lugar a limpeza, a otimização, o diagnóstico e a análise de disco do computador.

## Módulos

| Módulo | O que faz |
|---|---|
| **Início** | Painel com uso de CPU, RAM, GPU e discos em tempo real |
| **Informações** | Processador, placa-mãe, BIOS, memórias, GPU, discos e sensores |
| **Limpeza** | Temporários, caches, lixeira, restos de atualização e jogos órfãos |
| **Otimização** | Inicialização, serviços, tarefas agendadas, plano de energia, paginação e apps pré-instalados |
| **Processos** | Lista de processos com classificação e encerramento seguro |
| **Dinosize** | Análise de espaço em disco com árvore, treemap e migração de pastas |
| **Overclock** | Benchmarks, teste de estresse e ajuste de clock da GPU com busca automática de limite estável |
| **Personalização** | Temas, cor de destaque, layout da barra lateral e animações |

## Estrutura

```
src/
  OtimizadorPC.Core            Modelos e contratos (sem dependência de plataforma)
  OtimizadorPC.Infrastructure  Implementações para Windows (WMI, registro, sensores, disco)
  OtimizadorPC.App             Interface WPF (MVVM)
  OtimizadorPC.Setup           Instalador e desinstalador
tests/
  OtimizadorPC.Tests           Testes automatizados
```

## Requisitos para compilar

- Windows 10 ou 11 (x64)
- .NET SDK 10

```powershell
dotnet build OtimizadorPC.sln -c Release
dotnet test
```

## Licença

MIT — veja [LICENSE](LICENSE).
