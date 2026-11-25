;
; LZSS compressed SAP-R player - 18 streams (stereo, two POKEYs)
; ---------------------------------------------------------------
;
; Plays *_18.lz16 files written by SapRExport:
;   streams 0..8  -> POKEY 1 ($D200..$D208)
;   streams 9..17 -> POKEY 2 ($D210..$D218)
;
; Uses the same LZSS format as playlzs16.asm (8-bit offset, 8-bit length,
; min length 1), but with 18 history buffers of 256 bytes (4608 bytes total)
; and a 3-byte skip header (17 bits, channels 17..1).
;
; Entry points (for the test harness):
;   $0600  init_song   (first call, sets up the song)
;   $0603  play_frame  (one frame, call once per VBI)
;
; Assemble with MADS, the compressed song must be in song.lz16 next to this file:
;   mads player18.asm -o:player18.xex
;
    org $0600
    jmp init_song
    jmp play_frame

; UPD = updates per VBI: 2 for double-mode CMS (default), 1 for one row per frame.
UPD = 2

NCH = 18

    org $80
chn_copy    .ds NCH
chn_pos     .ds NCH
skip_flag   .ds NCH
bptr        .ds 2
cur_pos     .ds 1
bit_data    .ds 1
upd_cnt     .ds 1
hbyte       .ds 1
bitcount    .ds 1
hch         .ds 1

POKEY = $D200

    org $2000
buffers
    .ds 256 * NCH
outbuf
    .ds NCH

song_data
    ins 'song.lz16'
song_end

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; Read next byte of the stream into A (song_ptr = get_byte+1)
;
get_byte
    lda song_data+1
    inc song_ptr
    bne gb_skip
    inc song_ptr+1
gb_skip
    rts
song_ptr = get_byte + 1

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; Write outbuf[0..17] to the two POKEYs
;
output_regs
    ldx #8
or_base
    lda outbuf,x
    sta POKEY,x
    dex
    bpl or_base
    ldx #17
or_ext
    lda outbuf,x
    sta $D207,x           ; $D207+9 = $D210 ... $D207+17 = $D218
    dex
    cpx #8
    bne or_ext
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; Song initialization
;
init_song
    ; Skip flags: 3 header bytes, bit k -> channel 17-k, LSB first
    lda #17
    sta hch
    ldy #0
hdr_byte
    lda song_data,y
    sta hbyte
    lda #8
    sta bitcount
hdr_bit
    lsr hbyte             ; C = next flag
    lda #0
    adc #0                ; A = C
    ldx hch
    sta skip_flag,x
    dec hch
    beq hdr_done          ; channel 0 never has a flag
    dec bitcount
    bne hdr_bit
    iny
    jmp hdr_byte
hdr_done

    ; Stream data starts after the 3 header bytes
    lda #<(song_data+3)
    sta song_ptr
    lda #>(song_data+3)
    sta song_ptr+1

    ; Initial values, channels 17..0. Each one goes to outbuf and to ring slot 255.
    lda #>buffers
    sta bptr+1
    lda #0
    sta bptr
    ldx #17
init_ch
    jsr get_byte
    sta outbuf,x
    ldy #255
    sta (bptr),y
    lda #0
    sta chn_copy,x
    sta chn_pos,x
    inc bptr+1
    dex
    bpl init_ch

    lda #0
    sta cur_pos
    lda #1
    sta bit_data          ; sentinel bit
    jsr output_regs
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; Decode one frame (18 channels) and write the POKEYs
;
decode_update
    lda #>buffers
    sta bptr+1
    lda #0
    sta bptr
    ldx #17

chn_loop
    lda skip_flag,x
    bne skip_chn          ; constant stream: outbuf keeps its init value

    lda chn_copy,x
    bne do_copy_byte      ; still copying a match

    ; Decode a new literal/match
    lsr bit_data          ; next flag bit
    bne got_bit
    jsr get_byte          ; flag byte exhausted, refill
    ror                   ; carry (sentinel) in, flag bit out
    sta bit_data
got_bit
    jsr get_byte          ; literal value, or match offset
    bcs store             ; C=1: literal

    sta chn_pos,x         ; match offset
    jsr get_byte
    sta chn_copy,x        ; match length (1..256, 0 = 256)

do_copy_byte
    dec chn_copy,x
    inc chn_pos,x
    ldy chn_pos,x
    lda (bptr),y          ; copy from history

store
    ldy cur_pos
    sta outbuf,x
    sta (bptr),y

skip_chn
    inc bptr+1
    dex
    bpl chn_loop

    inc cur_pos
    jsr output_regs
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; One VBI: UPD updates (each update is one row, see decode_update)
;
play_frame
    lda #UPD
    sta upd_cnt
upd_loop
    jsr decode_update
    dec upd_cnt
    bne upd_loop
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; Main program (run address): init, then one frame per VBI until the song ends
;
wait_frame
    lda 20
delay
    cmp 20
    beq delay
    rts

start
    jsr init_song
main_loop
    jsr wait_frame
    jsr play_frame
    lda song_ptr+1
    cmp #>song_end
    bne main_loop
    lda song_ptr
    cmp #<song_end
    bne main_loop
    rts

    run start
