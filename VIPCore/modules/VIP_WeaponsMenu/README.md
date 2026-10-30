# Allows a VIP player to select weapons packages

The menu is a WASD menu and requires [CS2MenuManager](https://github.com/schwarper/CS2MenuManager) to be installed on the server (`addons/counterstrikesharp/shared/CS2MenuManager/`). Keys and colors are configured in its `config.toml`.

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