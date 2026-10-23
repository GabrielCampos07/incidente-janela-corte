# Teste de carga do pico de corte (gate de release)

Reproduz o padrão de 22/10: prévia de enquadramento em volume alto ao mesmo tempo que as ordens se concentram antes das 14h30. Reprova o release se o POST /ordens passar de 300 ms no p95 ou de 0,1% de erro.

```bash
brew install k6
k6 run \
  -e BASE_URL=https://api.homolog.portal \
  -e TOKEN=<token de um distribuidor de teste> \
  -e FUNDO_ID=<fundo de teste> \
  -e PREVIA_PICO_RPS=300 \
  -e ORDENS_PICO_RPS=40 \
  pico-corte.js
```

Calibrar `PREVIA_PICO_RPS` e `ORDENS_PICO_RPS` com a contagem por rota de 22/10 entre 14h00 e 14h30 (consulta 1 de `03-timeline`), com margem de 50%.

Quando rodar: em todo release que mexa na prévia, nas consultas de posições, no POST /ordens, no pool de conexões ou no health check. O resultado (p95, p99, taxa de erro e pico de `DatabaseConnections` em homologação) vai anexado ao PR.

Para provar que a correção funcionou: rodar a versão atual e a corrigida com o mesmo perfil e comparar a vazão máxima sustentada com p95 do POST abaixo de 300 ms.

Homologação nunca aponta para o sistema de processamento real: as ordens geradas aqui não podem sair do ambiente.
