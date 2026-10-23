# Entrega segura: feature flag, deploy progressivo e rollback ensaiado

Prioridade 4. O incidente teve três ingredientes de entrega: funcionalidade no caminho crítico sem flag, deploy no meio do dia perto do pico e nenhum rollback ensaiado.

## Feature flags

- AWS AppConfig (feature flags), lido pela API e pelo front com cache de 30 s.
- Obrigatório para qualquer funcionalidade que adicione chamadas ao backend no fluxo de boleta ou que mude o caminho do POST /ordens.
- Toda flag tem dono e data de remoção. Flag esquecida vira dívida.
- A primeira flag é a da prévia de enquadramento, com três estados: desligada, lote com debounce e por linha (legado, para remoção).

## Deploy progressivo

ECS blue/green com CodeDeploy:

```yaml
DeploymentConfig: CodeDeployDefault.ECSCanary10Percent5Minutes
AutoRollbackConfiguration:
  Enabled: true
  Events: [DEPLOYMENT_FAILURE, DEPLOYMENT_STOP_ON_ALARM]
Alarms:
  - portal-post-ordens-p95-alto
  - portal-post-ordens-slo-consumo-rapido
  - portal-npgsql-pool-esgotado
```

10% do tráfego vai para a versão nova por 5 minutos. Se qualquer alarme disparar, o CodeDeploy volta sozinho.

## Janela de congelamento

Nenhum deploy em produção entre 13h30 e 15h00 em dia útil, verificado pelo pipeline e não por combinado:

```yaml
- name: Bloquear deploy na janela de corte
  run: |
    hora=$(TZ=America/Sao_Paulo date +%H%M)
    dia=$(TZ=America/Sao_Paulo date +%u)
    if [ "$dia" -le 5 ] && [ "$hora" -ge 1330 ] && [ "$hora" -lt 1500 ]; then
      echo "Deploy bloqueado: janela de corte (13h30-15h00)"; exit 1
    fi
```

Exceção só para correção de incidente em andamento, com aprovação do tech lead registrada.

## Rollback ensaiado

- Uma vez por mês, em homologação e com hora marcada, alguém que não fez o último deploy executa o rollback seguindo o runbook. Mede-se o tempo até a versão anterior estar servindo.
- Meta: menos de 10 minutos.
- Migrations de banco seguem expandir e depois contrair: a versão anterior do código precisa funcionar com o schema novo. É isso que permite reverter o código sem reverter o banco.

## Runbooks

Cada alarme de `alarmes.yaml` aponta para um runbook com o mesmo formato: o que o alarme significa, o que olhar primeiro (com link para o painel), as ações seguras nos primeiros 10 minutos, o que não fazer e quem acionar. O runbook de contenção desta semana (`01-contencao`) é o primeiro.
