;
; LZSS compressed player - mono (9 streams, one POKEY) with optional pseudo stereo
; --------------------------------------------------------------------------------
;
; Plays the 9-stream files written by SapRExport for mono modules (*_s0_N.lz16).
;   streams 0..8 -> POKEY 1 ($D200..$D208), decoded and written every update
;
; PSEUDO = 0 : mono. Only POKEY 1 is written.
; PSEUDO = 1 : pseudo stereo. POKEY 2 ($D210..$D218) gets the same 9 values, delayed by one
;              frame. The delay is UPD rows: 1 row for UPD = 1, and 2 rows for UPD = 2 (double
;              mode, one frame = two rows). The buffers hist0 / hist1 hold the last rows.
;              At init both POKEYs get the initial values, so the first frame is not silent.
;
; Uses the same LZSS format as playlzs16.asm (8-bit offset, 8-bit length, min length 1),
; with 9 history buffers of 256 bytes and a 1-byte skip header (8 flags, channels 8..1).
;
; Entry points (for the test harness):
;   $0600  init_song   (first call, sets up the song)
;   $0603  play_frame  (one frame, call once per VBI)
;
; Build (MADS). The compressed song must be song_mono.lz16 next to this file:
;   mads player_mono.asm -o:player_mono.xex
; Settings are the two constants below. For a different setting, change the value and assemble again.

; UPD = updates per VBI: 1 for one row per frame CMC, 2 for double-mode DMC (default).
UPD = 2

; PSEUDO = 0 mono, PSEUDO = 1 pseudo stereo (POKEY 2 gets the values UPD rows late).
PSEUDO = 1

POKEY  = $D200
POKEY2 = $D210
NCH    = 9

    org $0600
    jmp init_song
    jmp play_frame

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

    org $2000
buffers
    .ds 256 * NCH
outbuf
    .ds NCH
    .if PSEUDO
hist0
    .ds NCH                ; values of the previous update
hist1
    .ds NCH                ; values of the update before that (used with UPD = 2)
    .endif

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
; Write outbuf[0..8] to POKEY 1, and to POKEY 2 when PSEUDO = 1
;
output_regs
    ldx #8
or_base
    lda outbuf,x
    sta POKEY,x
    dex
    bpl or_base

    .if PSEUDO
    .if UPD=1
    ; POKEY 2 gets the previous row, this row goes into hist0
    ldx #8
ps_1
    lda hist0,x
    sta POKEY2,x
    lda outbuf,x
    sta hist0,x
    dex
    bpl ps_1
    .else
    ; POKEY 2 gets the row from two updates ago (one frame); the history shifts by one row
    ldx #8
ps_2
    lda hist1,x
    sta POKEY2,x
    lda hist0,x
    sta hist1,x
    lda outbuf,x
    sta hist0,x
    dex
    bpl ps_2
    .endif
    .endif
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; Song initialization
;
init_song
	mva #$03 $d20f	;skctl init
	.if PSEUDO
	sta $d21f
	.endif
    ; Skip flags: 1 header byte, bit k -> channel 8-k, LSB first
    lda #NCH-1
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

    ; Stream data starts after the header byte
    lda #<(song_data+1)
    sta song_ptr
    lda #>(song_data+1)
    sta song_ptr+1

    ; Initial values, channels 8..0. Each one goes to outbuf and to ring slot 255.
    lda #>buffers
    sta bptr+1
    lda #0
    sta bptr
    ldx #NCH-1
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

    .if PSEUDO
    ; the delayed POKEY starts with the initial values too
    ldx #8
ps_init
    lda outbuf,x
    sta hist0,x
    sta hist1,x
    dex
    bpl ps_init
    .endif

    lda #0
    sta cur_pos
    lda #1
    sta bit_data          ; sentinel bit
    jsr output_regs
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; Decode one update (9 channels) and write the POKEYs
;
decode_update
    lda #>buffers
    sta bptr+1
    lda #0
    sta bptr
    ldx #NCH-1

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
	.if UPD > 1
    	ldx vcpause:#15 ;vcount lines (30 scanlines) wait between writes
@	lda vcount
@	cmp vcount
	beq @-
	dex
	bne @-1
	.endif
    dec upd_cnt
    bne upd_loop
    rts
vcount	equ $d40b
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
    jmp start ;rts

    run start
