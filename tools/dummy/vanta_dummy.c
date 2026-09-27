/* Vanta selftest dummy target. Harmless: it only changes its own variables.
   Prints the addresses of its state on the first line, then the values every second. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { char pad[16]; int health; float speed; char rest[0x100]; } Player;
Player* g_player;
double g_battery = 1000.0;
int g_xp = 0;
static char g_obj[0x200];

void drain(double* battery, double amount, char* obj);
void addxp(int* xp, int gain);
int get_health(void);
void damage(void);

int main(int argc, char** argv)
{
    int seconds = 600;
    for (int i = 1; i < argc; i++) if (!strcmp(argv[i], "--seconds") && i + 1 < argc) seconds = atoi(argv[++i]);
    g_player = (Player*)calloc(1, sizeof(Player));
    g_player->health = 100; g_player->speed = 1.0f;
    setvbuf(stdout, NULL, _IONBF, 0);
    printf("VANTA-DUMMY pid=%lu battery=%p xp=%p health=%p\n", GetCurrentProcessId(), (void*)&g_battery, (void*)&g_xp, (void*)&g_player->health);
    DWORD end = GetTickCount() + (DWORD)seconds * 1000;
    int tick = 0;
    while ((int)(end - GetTickCount()) > 0)
    {
        drain(&g_battery, 0.5, g_obj);
        addxp(&g_xp, 10);
        damage();
        if (g_player->health < 1) g_player->health = 100;
        if (++tick % 10 == 0) printf("battery=%.1f xp=%d health=%d\n", g_battery, g_xp, get_health());
        Sleep(50);
    }
    return 0;
}
