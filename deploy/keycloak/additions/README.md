# Other systems' realm files

Put other systems' parts of realm `fors` here (or in the directory `FORS_KEYCLOAK_ADDITIONS_DIR` names): Keycloak realm
representations with `"realm": "$(env:FORS_KEYCLOAK_REALM)"` (the realm's name, `fors` unless the installation kept `ume`; docs/upgrade-guide.md) and only that system's roles, clients, scope mappings and identity providers.
`realm-import` applies them after `../realm/*.json`, in name order, on every `docker compose ... up`. The values of
their `$(env:NAME)` placeholders go in the file `FORS_KEYCLOAK_IMPORT_ENV` names. See docs/keycloak.md, *Realm files*.

Files here are not part of this repository (`.gitignore`): each system delivers its own.
