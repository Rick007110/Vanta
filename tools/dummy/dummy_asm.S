# Vanta selftest dummy: code sites with the same byte patterns as real game cheats (Win64 ABI).
        .intel_syntax noprefix
        .text
        .globl  drain, addxp, get_health, damage

# void drain(double* battery, double amount, char* obj)   -- The Last Caretaker battery drain pattern
drain:
        push    rsi
        sub     rsp, 32
        movdqu  [rsp], xmm6
        movapd  xmm6, xmm1
        mov     rax, rcx
        mov     rsi, r8
        movsd   xmm0, qword ptr [rax]          # F2 0F 10 00
        subsd   xmm0, xmm6                      # F2 0F 5C C6   <- NOP'd by "Infinite Battery"
        movsd   qword ptr [rax], xmm0          # F2 0F 11 00
        mov     al, 1                           # B0 01
        .byte   0xC6, 0x86, 0x00, 0x01, 0x00, 0x00, 0x01   # mov byte ptr [rsi+0x100],1
        movdqu  xmm6, [rsp]
        add     rsp, 32
        pop     rsi
        ret

# void addxp(int* xp, int gain)   -- The Last Caretaker XP pattern: 03 C3 41 89 06 4C 8B 74 24 30
addxp:
        push    rbx
        push    r14
        sub     rsp, 0x38
        mov     [rsp+0x30], r14
        mov     r14, rcx
        mov     ebx, edx
        mov     eax, [r14]
        .byte   0x03, 0xC3                      # add eax,ebx
        .byte   0x41, 0x89, 0x06                # mov [r14],eax
        .byte   0x4C, 0x8B, 0x74, 0x24, 0x30    # mov r14,[rsp+30]
        add     rsp, 0x38
        pop     r14
        pop     rbx
        ret

# int get_health(void)  -- static pointer reached via RIP-relative load: 48 8B 05 ?? ?? ?? ?? 48 85 C0 74 03 8B 40 10
get_health:
        .byte   0x48, 0x8B, 0x05
        .long   g_player - (. + 4)
        .byte   0x48, 0x85, 0xC0, 0x74, 0x03, 0x8B, 0x40, 0x10
        ret

# void damage(void)  -- player->health -= 1 (health lives at player+0x10)
damage:
        mov     rax, [rip + g_player]
        test    rax, rax
        je      1f
        dec     dword ptr [rax+0x10]
1:      ret
