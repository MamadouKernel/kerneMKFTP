# Fiche de collecte - Configuration des armateurs

Remplis une ligne par armateur. Une fois complet, renvoie-moi ce fichier et je genere
le vrai armateurs.json pret a deployer.

## Connexion

| Code    | Protocole (sftp/ftpes) | Host                  | Port | Username | Nom du fichier mdp chiffre |
|---------|------------------------|-----------------------|------|----------|-----------------------------|
| MSC     |                        |                       |      |          | MSC.enc                     |
| MAERSK  |                        |                       |      |          | MAERSK.enc                  |
| CMA-CGM |                        |                       |      |          | CMA-CGM.enc                 |
| OOCL    |                        |                       |      |          | OOCL.enc                    |
| ONE     |                        |                       |      |          | ONE.enc                     |
| MEDLOG  |                        |                       |      |          | MEDLOG.enc                  |
| GUCE    | ftpes                  | files01.guce.gouv.ci | 990  | HDL-CIT  | GUCE.enc  (deja fait)        |
| DOUANE  |                        |                       |      |          | DOUANE.enc                  |

## EDI que TU ENVOIES a chaque armateur (ediEnvoyes)

Pour chaque type, indique : dossier(s) source local(aux) + chemin de depot distant.

| Code    | Type(s) EDI envoyes | Dossier(s) source local            | Chemin distant de depot |
|---------|----------------------|-------------------------------------|---------------------------|
| MSC     | ex: CODECO, COARRI   |                                      |                            |
| MAERSK  |                      |                                      |                            |
| CMA-CGM |                      |                                      |                            |
| OOCL    |                      |                                      |                            |
| ONE     |                      |                                      |                            |
| MEDLOG  |                      |                                      |                            |
| GUCE    | COARRI               | \\...\GUCE_CIT\COARRI\LOAD, \DISCH  | /actor-to-guce             |
| DOUANE  | ex: EDI_DOUANE       |                                      |                            |

## EDI que TU RECOIS de chaque armateur (ediRecus)

Pour chaque type, indique : chemin distant a surveiller + dossier local de reception.

| Code    | Type(s) EDI recus    | Chemin distant a surveiller | Dossier local de reception |
|---------|------------------------|-------------------------------|-------------------------------|
| MSC     | ex: COPARN, COREOR     |                                |                                |
| MAERSK  |                        |                                |                                |
| CMA-CGM |                        |                                |                                |
| OOCL    |                        |                                |                                |
| ONE     |                        |                                |                                |
| MEDLOG  |                        |                                |                                |
| GUCE    | COARRI                  | /guce-to-actor                | \\...\Prod\GUCE\COARRI\in     |
| DOUANE  | ex: MANIFESTE           |                                |                                |

---

## Notes

- Un armateur peut n'avoir QUE des EDI envoyes, QUE des EDI recus, ou les deux — pas
  d'obligation de symetrie.
- "DOUANE" n'est peut-etre pas un "armateur" a proprement parler mais techniquement
  il est traite exactement pareil dans le systeme (connexion + listes EDI envoyes/recus).
- Si plusieurs dossiers sources alimentent le meme type d'EDI vers le meme armateur
  (comme LOAD + DISCH pour COARRI/GUCE), liste-les tous, ils seront combines.
- Cote reception, si un armateur depose le meme type d'EDI dans plusieurs dossiers
  distants differents (chacun avec son propre dossier local de destination), c'est
  possible via "remoteSources" (voir exemple dans armateurs-squelette-8.json) au
  lieu d'un seul remotePath/localDestinationDir.
- Les reglages suivants sont modifiables par defaut pour toute l'application dans
  global-config.json, mais peuvent etre surcharges pour un armateur precis ou meme
  un seul type EDI si besoin : logRetentionDays (retention des logs), 
  archiveCompressAfterDays (retention avant compression des archives),
  minMinutesBetweenSameAlert (delai anti-repetition des alertes). Signale-moi si un
  armateur ou un flux a besoin d'un reglage different du reste.
