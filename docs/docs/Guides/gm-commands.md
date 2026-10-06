---
tags: [user]
---

# Commandes GM

Toutes les commandes s'utilisent avec `/` devant (ex: `/level 99`). Un compte avec un groupe de
permission suffisant (ex: `OperatorGroup`) est nécessaire pour la plupart d'entre elles - voir
[player-permission](./player-permission.md). Liste complète dans le code :
`src/Libraries/Game.Server/Commands/`.

## Commandes essentielles

| Commande | Effet |
|---|---|
| `/level <niveau> [cible]` | Fixe le niveau du joueur (soi-même par défaut) |
| `/advance` / `/a <cible> <niveaux>` | Fait monter de N niveaux |
| `/exp <valeur> [cible]` | Donne de l'expérience |
| `/gold <valeur> [cible]` | Donne de l'or |
| `/item <id_objet> [quantité]` | Ajoute un objet à ton inventaire |
| `/give <cible> <id_objet> [quantité]` | Donne un objet à un autre joueur |
| `/full_set` | Équipement complet de ta classe, +9, instantané |
| `/all_skills_master` | Toutes les compétences au niveau maître |
| `/setskill <id_comp> <niveau>` | Fixe le niveau d'une compétence précise (soi-même) |
| `/setskillother <cible> <id_comp> <niveau>` | Idem pour un autre joueur |
| `/skillup <id_comp>` | Monte une compétence d'un niveau |
| `/setjob <job>` | Change de classe (groupe de compétences) |
| `/r` / `/reset` | Remet HP/SP au maximum |
| `/stat <point>` | Ajoute un point de stat (utilisable même sans droits GM) |
| `/stat_reset` | Réinitialise les stats et rend les points |
| `/hp <valeur> [cible]` / `/sp <valeur> [cible]` | Fixe HP / SP |
| `/set_maxhp <valeur>` / `/set_maxsp <valeur>` | Fixe le HP/SP max temporairement |
| `/mspd <valeur>` | Change ta vitesse de déplacement |

## Téléportation

| Commande | Effet |
|---|---|
| `/goto -m <nom_map>` | Téléporte vers une map (nom partiel accepté ; spawn officiel de ton empire si dispo) |
| `/goto <x> <y>` | Téléporte à une position sur la map courante (unités = mètres depuis l'origine de la map) |
| `/tp <cible>` | Te téléporte vers un joueur |
| `/tphere <cible>` | Téléporte un joueur vers toi |

### Maps disponibles sur ce serveur (`data/atlasinfo.txt`)

Ce set de données de dev contient 6 maps, avec un mélange de monstres très large sur chacune (pas de
séparation propre par niveau comme sur le jeu officiel — ne pas se fier au niveau des mobs présents pour
juger la difficulty d'une map ici). Nom exact à utiliser avec `/goto -m` entre parenthèses :

| Map | Taille (cellules) | Description |
|---|---|---|
| **a1** (`metin2_map_a1`) | 4×5 | Un des 3 continents "maison" d'empire — grande map ouverte avec spawn de ville selon ton empire. Mix très large de monstres (loups/ours de base jusqu'à Demon King/Chief Esoteric Arahan). |
| **b1** (`metin2_map_b1`) | 4×5 | Deuxième continent d'empire, même format que a1. |
| **c1** (`metin2_map_c1`) | 4×5 | Troisième continent d'empire, même format. |
| **n_flame_01** (`metin2_map_n_flame_01`) | 6×6 | Map "n_" = instance/donjon à thème (préfixe officiel des maps hors-continent). Thème feu (nom "flame"). Contient des mobs de type feu/glace mélangés (Ice Devil, Ice Witch, Demon...). |
| **n_snowm_01** (`metin2_map_n_snowm_01`) | 6×6 | Donjon à thème neige/montagne ("snowm" = snow mountain). Mobs glace (Yeti, Ice Golem, Ice Witch...) mélangés à d'autres types. |
| **trent02** (`metin2_map_trent02`) | 3×3 | ⚠️ Pas un nom de map officiel connu — probablement une map custom/placeholder de ce jeu de données de dev, pas du jeu original. À vérifier avant d'en tirer des conclusions de lore. |

Astuce : `/goto -m` accepte un nom partiel ; si l'appel est ambigu (plusieurs maps correspondent), il liste
les noms possibles au lieu de te téléporter.

## Monstres / combat

| Commande | Effet |
|---|---|
| `/m <id> [nb]` / `/mob <id> [nb]` / `/spawn <id> [nb]` | Fait apparaître un monstre/PNJ près de toi |
| `/mob_ld <id> [x] [y] [rotation]` | Spawn à une position précise |
| `/mm <id>` | Spawn à une position aléatoire sur la map courante |
| `/group <id_groupe>` / `/grrandom <id_groupe>` | Spawn un groupe de monstres prédéfini |
| `/pull` / `/pull_monster` | Fait venir instantanément tous les monstres à portée vers toi |
| `/attract_ranger` | Rend agressifs tous les monstres à distance autour de toi et les cible sur toi |
| `/forgetme` | Efface l'aggro de tous les monstres qui te ciblent |
| `/weak` / `/weaken` | Met la vie de tous les monstres proches à 1 |
| `/purge [all]` | Supprime les monstres autour de toi (`all` = toute la map) |

## Monture

| Commande | Effet |
|---|---|
| `/mount [id_mob]` | Monte ton cheval (ou une monture custom via un id de proto monstre) |
| `/dismount` | Descend de monture |
| `/sethorselevel <0-30>` | Fixe le niveau de ton cheval (débloque `/mount`) |

## Debug / diagnostic

| Commande | Effet |
|---|---|
| `/state` | Affiche tes stats et infos de personnage actuelles |
| `/debug_damage` | Active/désactive le détail de calcul de dégâts coup par coup dans le chat |
| `/who` | Compte de joueurs connectés (ce core + par empire + total) |
| `/user` | Liste tous les joueurs connectés sur ce core serveur |
| `/gstate <nom_guilde>` | Infos sur une guilde |

## Perso / cosmétique

| Commande | Effet |
|---|---|
| `/motion <id>` | Joue une animation précise sur ton personnage |
| `/dance1` (et autres émotes) | Emotes prédéfinies |
| `/str+` / `/dex+` / `/int+` / `/con+` | +1 point dans la stat correspondante |

## Admin serveur

| Commande | Effet |
|---|---|
| `/shutdown` | Arrêt propre du serveur (sauvegarde tous les joueurs connectés d'abord — **toujours préférer à un kill forcé**) |
| `/kick <cible>` / `/dc <cible>` | Déconnecte un joueur |
| `/kill <cible>` | Tue un joueur |
| `/notice <message>` [-b] | Diffuse un message à tous les joueurs connectés (`-b` = notice centrée en grand) |
| `/reload_permissions` | Recharge les permissions sans redémarrer |
| `/ip` / `/ipurge` | Vide l'inventaire et déséquipe tout |

## Perso — sans droits GM (utilisables par tout joueur)

| Commande | Effet |
|---|---|
| `/stat <point>` | Ajoute un point de stat |
| `/restart_here` | Réapparaît sur place après la mort |
| `/restart_town` | Réapparaît en ville après la mort |
| `/phase_select` | Retour à la sélection de personnage |
| `/logout` / `/quit` | Quitte le jeu |
| `/in_game_mall` | Ouvre la page web de la boutique en jeu |
