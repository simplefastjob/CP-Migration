# NovoERP Builder

Esta pasta executa a segunda etapa do CP Migration.

## Entrada

Os arquivos compactados gerados pelo primeiro pipeline:

`output/Migration/CONSOLIDADO/*.ndjson.gz`

## Execução

Dê dois cliques em:

`GERAR_NOVO_ERP.cmd`

## Saída

A pasta `NovoERP` será criada na raiz contendo:

- `NovoERP.sqlite`: banco intermediário limpo para o ERP novo;
- `builder-summary.json`: contagem dos registros por domínio;
- `LEIA-ME.txt`: descrição do resultado.

O banco contém uma tabela geral `documents`, que preserva todos os documentos consolidados, e tabelas especializadas iniciais:

- `entities`
- `products`
- `stock`
- `financial`
- `purchases`
- `sales`
- `fiscal`

Campos ainda não identificados permanecem preservados em JSON. Isso evita perda de informação enquanto os mapeamentos definitivos do ERP novo são aprofundados.
