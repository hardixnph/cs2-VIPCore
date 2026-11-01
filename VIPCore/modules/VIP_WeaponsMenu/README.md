# Allows a VIP player to select weapons packages

The menu requires [MenuManagerCS2](https://github.com/NickFox007/MenuManagerCS2) to be installed on the server. The menu type (button/WASD, chat, center...) and its keys are set in MenuManagerCS2's config, players can pick their own type there too. Menus close after 15 seconds.

# Config

### in vip.json
```
              "WeaponsMenu": {
                  "CT": {
                      "AK47 + DEAGLE": {
                          "Weapons": [
                              "weapon_ak47",
                              "weapon_deagle"
                          ],
                          "Round": 2
                      },
                      "M4A1 + DEAGLE": {
                          "Weapons": [
                              "weapon_m4a1",
                              "weapon_deagle"
                          ],
                          "Round": 1
                      }
                  },
                  "T": {
                      "AWP + DEAGLE": {
                          "Weapons": [
                              "weapon_awp",
                              "weapon_deagle"
                          ],
                          "Round": 2
                      },
                      "SCOUT + DEAGLE": {
                          "Weapons": [
                              "weapon_ssg08",
                              "weapon_deagle"
                          ],
                          "Round": 1
                      }
                  }
              }
```