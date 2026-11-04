# Sirius

IDE nativa para Windows, feita em C# com Avalonia, sem WebView nem Electron.

## Como rodar

Precisa do [SDK do .NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Sirius
```

## Atalhos

| Atalho | Ação |
|---|---|
| `Ctrl K` | Barra de comando: encontrar arquivos e executar ações |
| `Ctrl O` | Abrir pasta |
| `Ctrl N` | Nova aba sem nome |
| `Ctrl S` | Salvar |
| `Ctrl Shift S` | Salvar como |
| `Ctrl W` | Fechar aba |
| `Ctrl F` | Procurar no arquivo |
| `Ctrl Shift F` | Buscar em todo o projeto (`Enter` avança pelos resultados) |
| `Ctrl .` | Modo foco |
| `F5` | Executar ou parar o projeto |
| `Ctrl J` | Abrir ou fechar o terminal |
| `Ctrl Shift G` | Painel de Git: mudanças, comparação, commit, enviar e receber |
| `Ctrl +` e `Ctrl -` | Aumentar ou diminuir a fonte do editor (`Ctrl 0` volta ao padrão) |
| `F2` | Renomear o item da lista de arquivos ou, no editor, o símbolo sob o cursor |
| `F12` ou `Ctrl` + clique | Ir para a definição |
| `Ctrl Espaço` | Mostrar sugestões |

## Inteligência de código

Autocompletar, erros no código, ir para a definição e renomear símbolo usam servidores de linguagem. Quando falta o servidor de um arquivo aberto, a cápsula oferece a instalação para C#, PHP, JavaScript, TypeScript e Python. Go, Rust e C++ funcionam se `gopls`, `rust-analyzer` ou `clangd` estiverem instalados.

## Testes

```bash
dotnet test
```

## Fontes

Geist e IBM Plex Mono são distribuídas sob a licença SIL Open Font. As licenças estão em `src/Sirius/Assets/Fonts`.
