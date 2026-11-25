;
; LZSS compressed SAP-R player - stereo interleaved (9 streams, 2 rows per frame)
; -------------------------------------------------------------------------------
;
; Plays *_il.lz16 files written by SapRExport. The stream is a normal 9-stream
; LZSS stream (same format as playlzs16.asm) with two rows per frame:
;   row 2f   -> base POKEY  ($D200..$D208)
;   row 2f+1 -> stereo POKEY ($D210..$D218)
; Each VBI decodes both rows, one after the other.
;
; Entry points (for the test harness):
;   $0600  init_song   (first call, sets up the song)
;   $0603  play_frame  (one frame, call once per VBI)
;
; Assemble with MADS, the compressed song must be in song.lz16 next to this file:
;   mads player_il.asm -o:player_il.xex
;
    org $0600
    jmp init_song
    jmp play_frame

; UPD = updates per VBI: 2 for double-mode CMS (default), 1 for one row per frame.
UPD = 2

NCH = 9

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

song_data
    ins 'song_il.lz16'
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
; Write outbuf[0..8] to the base POKEY ($D200) or the stereo POKEY ($D210)
;
out_base
    ldx #8
ob_loop
    lda outbuf,x
    sta $D200,x
    dex
    bpl ob_loop
    rts

out_ext
    ldx #8
oe_loop
    lda outbuf,x
    sta $D210,x
    dex
    bpl oe_loop
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; Song initialization: row 0 (base init values) and row 1 (first stereo row)
;
init_song
	mva #$03 $d20f	;skctl init
	sta $d21f
    ; Skip flags: 1 header byte, bit k -> channel 8-k, LSB first
    lda #8
    sta hch
    ldy #0
hdr_byte
    lda song_data,y
    sta hbyte
    lda #8
    sta bitcount
hdr_bit
    lsr hbyte
    lda #0
    adc #0
    ldx hch
    sta skip_flag,x
    dec hch
    beq hdr_done          ; channel 0 never has a flag
    dec bitcount
    bne hdr_bit
    iny
    jmp hdr_byte
hdr_done

    ; Stream data starts after the 1 header byte
    lda #<(song_data+1)
    sta song_ptr
    lda #>(song_data+1)
    sta song_ptr+1

    ; Initial values, channels 8..0 (row 0)
    lda #>buffers
    sta bptr+1
    lda #0
    sta bptr
    ldx #8
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
    jsr out_base

    ; Row 1 is the first stereo row
    jsr decode_row
    jsr out_ext
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; Decode one row (9 channels) into outbuf and the history buffers
;
decode_row
    lda #>buffers
    sta bptr+1
    lda #0
    sta bptr
    ldx #8

chn_loop
    lda skip_flag,x
    bne skip_chn          ; constant stream: outbuf keeps its init value

    lda chn_copy,x
    bne do_copy_byte      ; still copying a match

    lsr bit_data          ; next flag bit
    bne got_bit
    jsr get_byte          ; flag byte exhausted, refill
    ror
    sta bit_data
got_bit
    jsr get_byte          ; literal value, or match offset
    bcs store             ; C=1: literal

    sta chn_pos,x
    jsr get_byte
    sta chn_copy,x

do_copy_byte
    dec chn_copy,x
    inc chn_pos,x
    ldy chn_pos,x
    lda (bptr),y

store
    ldy cur_pos
    sta outbuf,x
    sta (bptr),y

skip_chn
    inc bptr+1
    dex
    bpl chn_loop

    inc cur_pos
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; One frame: base row, then stereo row
;
decode_update
    jsr decode_row
    jsr out_base
    jsr decode_row
    jsr out_ext
    rts

;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
; One VBI: UPD updates (each update is one row, see decode_update)
;
play_frame
    lda #UPD
    sta upd_cnt
upd_loop
    jsr decode_update
	ldx vcpause:#15 ;vcount lines (30 scanlines) wait between writes
@	lda vcount
@	cmp vcount
	beq @-
	dex
	bne @-1
    dec upd_cnt
    jne upd_loop
    rts

consol	equ $d01f
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
	mva #8 consol
    jsr wait_frame
    jsr play_frame
    write_addr
    lda consol
    cmp con_old:#0
    beq cont	;skip
    cmp #5
    beq plus
    cmp #6 
    beq minus
    sta con_old       
cont    
    lda song_ptr+1
    cmp #>song_end
    bne main_loop
    lda song_ptr
    cmp #<song_end
    bne main_loop
    jmp start

plus
	sta con_old
	lda vcpause
	cmp #66
	beq cont
	inc vcpause
	Write
	jmp cont

minus
	sta con_old
	lda vcpause
	cmp #1
	beq cont
	dec vcpause
	Write
	jmp cont

.proc	Write
	ldx #0
	lda vcpause
@	a_lt #10 go
	inx
	sub #10
	jmp @-
go
	ora #$10
	sta $bc50
	txa
	ora #$10
	sta $bc4f
	rts	
.endp

.proc	write_addr
	lda song_ptr+1
:4	lsr @
	gethex
	sta $bc40+48
	lda song_ptr+1
	and #$0f
	gethex
	sta $bc40+49

	lda song_ptr
:4	lsr @
	gethex
	sta $bc40+50
	lda song_ptr
	and #$0f
	gethex
	sta $bc40+51
	rts
.endp

.proc	gethex
	a_ge #10 letter
	ora #$10
	rts
letter	sub #10
	add #$21
	rts
.endp

	icl "matosimi_macros.asx"
	
	org $bc40
	dta d' vcount delay: 15     ',d'lzss sdcmc player'*,0
	dta d' addr: $ffff                            '
:40	dta 0
	dta d' SELECT/START to change vcount delay.    '
	
    run start
