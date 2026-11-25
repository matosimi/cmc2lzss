// Generated automatically with "cito". Do not edit.
using System;
using System.Text;
namespace Sf.Asap
{

	enum NmiStatus
	{
		Reset,
		OnVBlank,
		WasVBlank
	}

	enum ASAPModuleType
	{
		SapB,
		SapC,
		SapD,
		SapS,
		Cmc,
		Cm3,
		Cmr,
		Cms,
		Dlt,
		Mpt,
		Rmt,
		Tmc,
		Tm2,
		Fc
	}

	/// <summary>Format of output samples.</summary>
	public enum ASAPSampleFormat
	{
		/// <summary>Unsigned 8-bit.</summary>
		U8,
		/// <summary>Signed 16-bit little-endian.</summary>
		S16LE,
		/// <summary>Signed 16-bit big-endian.</summary>
		S16BE
	}

	/// <summary>Atari 8-bit chip music emulator.</summary>
	/// <remarks>This class performs no I/O operations - all music data must be passed in byte arrays.</remarks>
	public class ASAP
	{
		public ASAP()
		{
			this.SilenceCycles = 0;
			this.Cpu.Asap = this;
		}

		/// <summary>Output sample rate.</summary>
		public const int SampleRate = 44100;

		internal int NextEventCycle;

		public Cpu6502 Cpu = new Cpu6502();

		int NextScanlineCycle;

		NmiStatus Nmist;

		int Consol;

		readonly byte[] Covox = new byte[4];

		readonly PokeyPair Pokeys = new PokeyPair();

		readonly ASAPInfo ModuleInfo = new ASAPInfo();

		int NextPlayerCycle;

		int TmcPerFrameCounter;

		int CurrentSong;

		int CurrentDuration;

		int BlocksPlayed;

		int SilenceCycles;

		int SilenceCyclesCounter;

		bool GtiaOrCovoxPlayedThisFrame;

		byte nmien;

		public void LoadOsRom(string filename)
		{
			Array.Clear(this.Cpu.Memory, 0, 65536);
			byte[] romData = System.IO.File.ReadAllBytes(filename);
			Array.Copy(romData, 0, this.Cpu.Memory, 0xC000, romData.Length);

			//initialize ROM stuff

			//disable other interrupts
			Cpu.SetInterruptFlag(true);
			Cpu.OsRomInit = true;
			Call6502(Cpu.Memory[0xFFFC] + (Cpu.Memory[0xFFFD] << 8)); //start vector
			while (Cpu.OsRomInit)
			{
				int amount = 144 * 312;
				Cpu.DoFrame(amount);
				Cpu.Cycle -= amount;
				//DoVbi();
			}
			Cpu.SetInterruptFlag(false);

		}

		public void LoadXex(string filename)
		{
			byte[] xexData = System.IO.File.ReadAllBytes(filename);
			int xexIndex = 2;
			int xexLength = xexData.Length;
			while (xexIndex + 5 <= xexLength)
			{
				int startAddr = ASAPInfo.GetWord(xexData, xexIndex);
				int blockLen = ASAPInfo.GetWord(xexData, xexIndex + 2) + 1 - startAddr;
				if (blockLen <= 0 || xexIndex + blockLen > xexLength)
					throw new Exception("Invalid binary block");
				xexIndex += 4;
				Array.Copy(xexData, xexIndex, Cpu.Memory, startAddr, blockLen);
				xexIndex += blockLen;
				if (xexIndex == xexLength)
					return;
				if (xexIndex + 7 <= xexLength && xexData[xexIndex] == 255 && xexData[xexIndex + 1] == 255)
					xexIndex += 2;
			}
			throw new Exception("Invalid binary block");
		}

		//perform VBI stuff
		internal void DoVbi()
		{
			if ((nmien & 0x40) != 0)  //nmien
			{
				//Cpu.trace = true;
				//Console.WriteLine("VBI on PC=" + Cpu.Pc.ToString("X"));
				Cpu.PushPc();
				Cpu.PushFlags(0);
				Cpu.Pc = Cpu.Memory[0xFFFA] + (Cpu.Memory[0xFFFB] << 8);
				//Cpu.trace = false;
			}
		}


		/**
		/// Enables silence detection.
		/// Causes playback to stop after the specified period of silence.
		 * @param seconds Length of silence which ends playback. Zero disables silence detection.
		 */
		public void DetectSilence(int seconds)
		{
			this.SilenceCyclesCounter = this.SilenceCycles = seconds * 1773447;
		}

		internal int PeekHardware(int addr)
		{
			switch (addr & 65311) {
			case 53268:
				return this.ModuleInfo.IsNtsc() ? 15 : 1;
			case 53279:
				return ~this.Consol & 15;
			case 53770:
			case 53786:
			case 53774:
			case 53790:
				return this.Pokeys.Peek(addr, this.Cpu.Cycle);
			case 53772:
			case 53788:
			case 53775:
			case 53791:
				return 255;
			case 54283:
			case 54299:
				int cycle = this.Cpu.Cycle;
				if (cycle > (this.ModuleInfo.IsNtsc() ? 29868 : 35568))
					return 0;
				return cycle / 228;
			case 54287:
			case 54303:
				switch (this.Nmist) {
				case NmiStatus.Reset:
					return 31;
				case NmiStatus.WasVBlank:
					return 95;
				default:
					return this.Cpu.Cycle < 28291 ? 31 : 95;
				}
			default:
				return this.Cpu.Memory[addr];
			}
		}

		internal void PokeHardware(int addr, int data)
		{
			if (addr >> 8 == 210) {
				// SAP-R export: report every raw write, before any de-duplication in Pokey.Poke
				if (this.PokeyWriteHook != null)
					this.PokeyWriteHook(addr, data);
				int t = this.Pokeys.Poke(addr, data, this.Cpu.Cycle);
				if (this.NextEventCycle > t)
					this.NextEventCycle = t;
			}
			else if ((addr & 65295) == 54282) {
				int x = this.Cpu.Cycle % 114;
				this.Cpu.Cycle += (x <= 106 ? 106 : 220) - x;
			}
			else if ((addr & 65295) == 54287) {
				this.Nmist = this.Cpu.Cycle < 28292 ? NmiStatus.OnVBlank : NmiStatus.Reset;
			}
			//shit
			else if ((addr & 65295) == 0xD40E)
			{
				nmien = (byte)data;
			}
			//-shit
			else if ((addr & 65280) == this.ModuleInfo.GetCovoxAddress()) {
				Pokey pokey;
				addr &= 3;
				if (addr == 0 || addr == 3)
					pokey = this.Pokeys.BasePokey;
				else
					pokey = this.Pokeys.ExtraPokey;
				int delta = data - this.Covox[addr];
				if (delta != 0) {
					pokey.AddDelta(this.Pokeys, this.Cpu.Cycle, delta << 17);
					this.Covox[addr] = (byte) data;
					this.GtiaOrCovoxPlayedThisFrame = true;
				}
			}
			else if ((addr & 65311) == 53279) {
				int delta = ((this.Consol & 8) - (data & 8)) << 20;
				if (delta != 0) {
					int cycle = this.Cpu.Cycle;
					this.Pokeys.BasePokey.AddDelta(this.Pokeys, cycle, delta);
					this.Pokeys.ExtraPokey.AddDelta(this.Pokeys, cycle, delta);
					this.GtiaOrCovoxPlayedThisFrame = true;
				}
				this.Consol = data;
			}
			else if (addr < 0xC000)
				this.Cpu.Memory[addr] = (byte) data;
		}

		internal void Call6502(int addr)
		{
			this.Cpu.Memory[53760] = 32;
			this.Cpu.Memory[53761] = (byte) addr;
			this.Cpu.Memory[53762] = (byte) (addr >> 8);
			this.Cpu.Memory[53763] = 210;
			this.Cpu.Pc = 53760;
		}

		void Call6502Player()
		{
			int player = this.ModuleInfo.Player;
			switch (this.ModuleInfo.Type) {
			case ASAPModuleType.SapB:
				Call6502(player);
				break;
			case ASAPModuleType.SapC:
			case ASAPModuleType.Cmc:
			case ASAPModuleType.Cm3:
			case ASAPModuleType.Cmr:
			case ASAPModuleType.Cms:
				Call6502(player + 6);
				break;
			case ASAPModuleType.SapD:
				if (player >= 0) {
					this.Cpu.PushPc();
					this.Cpu.Memory[53760] = 8;
					this.Cpu.Memory[53761] = 72;
					this.Cpu.Memory[53762] = 138;
					this.Cpu.Memory[53763] = 72;
					this.Cpu.Memory[53764] = 152;
					this.Cpu.Memory[53765] = 72;
					this.Cpu.Memory[53766] = 32;
					this.Cpu.Memory[53767] = (byte) player;
					this.Cpu.Memory[53768] = (byte) (player >> 8);
					this.Cpu.Memory[53769] = 104;
					this.Cpu.Memory[53770] = 168;
					this.Cpu.Memory[53771] = 104;
					this.Cpu.Memory[53772] = 170;
					this.Cpu.Memory[53773] = 104;
					this.Cpu.Memory[53774] = 64;
					this.Cpu.Pc = 53760;
				}
				break;
			case ASAPModuleType.SapS:
				int i = this.Cpu.Memory[69] - 1;
				this.Cpu.Memory[69] = (byte) i;
				if (i == 0)
					this.Cpu.Memory[45179] = (byte) (this.Cpu.Memory[45179] + 1);
				break;
			case ASAPModuleType.Dlt:
				Call6502(player + 259);
				break;
			case ASAPModuleType.Mpt:
			case ASAPModuleType.Rmt:
			case ASAPModuleType.Tm2:
			case ASAPModuleType.Fc:
				Call6502(player + 3);
				break;
			case ASAPModuleType.Tmc:
				if (--this.TmcPerFrameCounter <= 0) {
					this.TmcPerFrameCounter = this.Cpu.Memory[this.ModuleInfo.GetMusicAddress() + 31];
					Call6502(player + 3);
				}
				else
					Call6502(player + 6);
				break;
			}
		}

		internal bool IsIrq()
		{
			return this.Pokeys.BasePokey.Irqst != 255;
		}

		internal void HandleEvent()
		{
			int cycle = this.Cpu.Cycle;
			if (cycle >= this.NextScanlineCycle) {
				if (cycle - this.NextScanlineCycle < 50)
					this.Cpu.Cycle = cycle += 9;
				this.NextScanlineCycle += 114;
				if (cycle >= this.NextPlayerCycle) {

					//DoVbi();    //this dovbi caused endless vbi loop
					Call6502Player(); 
					this.NextPlayerCycle += 114 * this.ModuleInfo.GetPlayerRateScanlines();
				}
			}
			int nextEventCycle = this.NextScanlineCycle;
			nextEventCycle = this.Pokeys.BasePokey.CheckIrq(cycle, nextEventCycle);
			nextEventCycle = this.Pokeys.ExtraPokey.CheckIrq(cycle, nextEventCycle);
			this.NextEventCycle = nextEventCycle;
		}

		int Do6502Frame()
		{
			this.NextEventCycle = 0;
			this.NextScanlineCycle = 0;
			this.Nmist = this.Nmist == NmiStatus.Reset ? NmiStatus.OnVBlank : NmiStatus.WasVBlank;
			int cycles = this.ModuleInfo.IsNtsc() ? 29868 : 35568;
			this.Cpu.DoFrame(cycles);
			this.Cpu.Cycle -= cycles;
			if (this.NextPlayerCycle != 8388608)
				this.NextPlayerCycle -= cycles;
			for (int i = 3;; i >>= 1) {
				this.Pokeys.BasePokey.Channels[i].EndFrame(cycles);
				this.Pokeys.ExtraPokey.Channels[i].EndFrame(cycles);
				if (i == 0)
					break;
			}
			return cycles;
		}

		int DoFrame()
		{
			this.GtiaOrCovoxPlayedThisFrame = false;
			this.Pokeys.StartFrame();
			int cycles = Do6502Frame();
			this.Pokeys.EndFrame(cycles);
			return cycles;
		}

		/// <summary>SAP-R export: optional callback for every POKEY write ($D200-$D21F), receives (addr, data).</summary>
		public Action<int, int> PokeyWriteHook;

		/// <summary>SAP-R export: address of the replay update routine (-1 = none). Called once per update.</summary>
		internal int PlayerUpdateAddr = -1;

		/// <summary>SAP-R export: called every time the CPU enters PlayerUpdateAddr.</summary>
		public Action PlayerUpdateHook;

		internal void OnPlayerUpdate()
		{
			if (this.PlayerUpdateHook != null)
				this.PlayerUpdateHook();
		}

		/// <summary>
		/// Test harness: runs a 6502 routine at pc until it returns (RTS) to the KIL at $D200.
		/// No ASAP player is called meanwhile. Writes to $D2xx go to PokeyWriteHook.
		/// </summary>
		bool harnessReady;

		public void RunRoutine(int pc, int cycleLimit)
		{
			if (!this.harnessReady)
			{
				this.Pokeys.Initialize(true, true);   // what PlaySong does before running the init routine
				this.harnessReady = true;
			}
			this.NextPlayerCycle = 8388608;
			this.Cpu.Memory[53760] = 210;   // KIL: spins when the routine returns here
			this.Cpu.Memory[510] = 255;     // return address 0xD1FF (RTS -> 0xD200)
			this.Cpu.Memory[511] = 209;
			this.Cpu.S = 253;
			this.Cpu.SetInterruptFlag(true);   // no IRQs: POKEY IRQ status is not initialised here
			this.Cpu.Pc = pc;
			this.Cpu.Cycle = 0;
			this.NextScanlineCycle = 0;
			this.NextEventCycle = 0;
			this.Cpu.DoFrame(cycleLimit);
			if (this.Cpu.Pc != 53760)
				throw new Exception("routine did not return, PC=" + this.Cpu.Pc.ToString("X") + " S=" + this.Cpu.S + " cycle=" + this.Cpu.Cycle + " A=" + this.Cpu.A + " X=" + this.Cpu.X + " Y=" + this.Cpu.Y);
		}

		/// <summary>SAP-R export: runs exactly one emulated video frame (no audio generation needed).</summary>
		public int RunFrame()
		{
			return DoFrame();
		}

		/**
		/// Loads music data ("module").
		 * @param filename Filename, used to determine the format.
		 * @param module Contents of the file.
		 * @param moduleLen Length of the file.
		 */
		public void Load(string filename, byte[] module, int moduleLen)
		{
			this.ModuleInfo.Load(filename, module, moduleLen);
			byte[] playerRoutine = ASAP6502.GetPlayerRoutine(this.ModuleInfo);
			if (playerRoutine != null) {
				int player = ASAPInfo.GetWord(playerRoutine, 2);
				int playerLastByte = ASAPInfo.GetWord(playerRoutine, 4);
				int music = this.ModuleInfo.GetMusicAddress();
				if (music <= playerLastByte)
					throw new Exception("Module address conflicts with the player routine");
				this.Cpu.Memory[19456] = 0;
				if (this.ModuleInfo.Type == ASAPModuleType.Fc)
					Array.Copy(module, 0, this.Cpu.Memory, music, moduleLen);
				else
					Array.Copy(module, 6, this.Cpu.Memory, music, moduleLen - 6);
				Array.Copy(playerRoutine, 6, this.Cpu.Memory, player, playerLastByte + 1 - player);
				if (this.ModuleInfo.Player < 0)
					this.ModuleInfo.Player = player;
				return;
			}
			Array.Clear(this.Cpu.Memory, 0, 65536);
			int moduleIndex = this.ModuleInfo.HeaderLen + 2;
			while (moduleIndex + 5 <= moduleLen) {
				int startAddr = ASAPInfo.GetWord(module, moduleIndex);
				int blockLen = ASAPInfo.GetWord(module, moduleIndex + 2) + 1 - startAddr;
				if (blockLen <= 0 || moduleIndex + blockLen > moduleLen)
					throw new Exception("Invalid binary block");
				moduleIndex += 4;
				Array.Copy(module, moduleIndex, this.Cpu.Memory, startAddr, blockLen);
				moduleIndex += blockLen;
				if (moduleIndex == moduleLen)
					return;
				if (moduleIndex + 7 <= moduleLen && module[moduleIndex] == 255 && module[moduleIndex + 1] == 255)
					moduleIndex += 2;
			}
			throw new Exception("Invalid binary block");
		}

		/**
		/// Returns information about the loaded module.
		 */
		public ASAPInfo GetInfo()
		{
			return this.ModuleInfo;
		}

		void Do6502Init(int pc, int a, int x, int y)
		{
			this.Cpu.Pc = pc;
			this.Cpu.A = a & 255;
			this.Cpu.X = x & 255;
			this.Cpu.Y = y & 255;
			this.Cpu.Memory[53760] = 210;
			this.Cpu.Memory[510] = 255;
			this.Cpu.Memory[511] = 209;
			this.Cpu.S = 253;
			for (int frame = 0; frame < 50; frame++) {
				Do6502Frame();
				if (this.Cpu.Pc == 53760)
					return;
			}
			throw new Exception("INIT routine didn't return");
		}

		/**
		/// Mutes the selected POKEY channels.
		 * @param mask An 8-bit mask which selects POKEY channels to be muted.
		 */
		public void MutePokeyChannels(int mask)
		{
			this.Pokeys.BasePokey.Mute(mask);
			this.Pokeys.ExtraPokey.Mute(mask >> 4);
		}

		/**
		/// Prepares playback of the specified song of the loaded module.
		 * @param song Zero-based song index.
		 * @param duration Playback time in milliseconds, -1 means infinity.
		 */
		public void PlaySong(int song, int duration)
		{
			if (song < 0 || song >= this.ModuleInfo.GetSongs())
				throw new Exception("Song number out of range");
			this.CurrentSong = song;
			this.CurrentDuration = duration;
			this.NextPlayerCycle = 8388608;
			this.BlocksPlayed = 0;
			this.SilenceCyclesCounter = this.SilenceCycles;
			this.Cpu.Reset();
			this.Nmist = NmiStatus.OnVBlank;
			this.Consol = 8;
			this.Covox[0] = 128;
			this.Covox[1] = 128;
			this.Covox[2] = 128;
			this.Covox[3] = 128;
			this.Pokeys.Initialize(this.ModuleInfo.IsNtsc(), this.ModuleInfo.GetChannels() > 1);
			MutePokeyChannels(255);
			int player = this.ModuleInfo.Player;
			int music = this.ModuleInfo.GetMusicAddress();
			switch (this.ModuleInfo.Type) {
			case ASAPModuleType.SapB:
				Do6502Init(this.ModuleInfo.GetInitAddress(), song, 0, 0);
				break;
			case ASAPModuleType.SapC:
			case ASAPModuleType.Cmc:
			case ASAPModuleType.Cm3:
			case ASAPModuleType.Cmr:
			case ASAPModuleType.Cms:
				Do6502Init(player + 3, 112, music, music >> 8);
				Do6502Init(player + 3, 0, song, 0);
				break;
			case ASAPModuleType.SapD:
			case ASAPModuleType.SapS:
				this.Cpu.Pc = this.ModuleInfo.GetInitAddress();
				this.Cpu.A = song;
				this.Cpu.X = 0;
				this.Cpu.Y = 0;
				this.Cpu.S = 255;
				break;
			case ASAPModuleType.Dlt:
				Do6502Init(player + 256, 0, 0, this.ModuleInfo.SongPos[song]);
				break;
			case ASAPModuleType.Mpt:
				Do6502Init(player, 0, music >> 8, music);
				Do6502Init(player, 2, this.ModuleInfo.SongPos[song], 0);
				break;
			case ASAPModuleType.Rmt:
				Do6502Init(player, this.ModuleInfo.SongPos[song], music, music >> 8);
				break;
			case ASAPModuleType.Tmc:
			case ASAPModuleType.Tm2:
				Do6502Init(player, 112, music >> 8, music);
				Do6502Init(player, 0, song, 0);
				this.TmcPerFrameCounter = 1;
				break;
			case ASAPModuleType.Fc:
				Do6502Init(player, song, 0, 0);
				break;
			}
			MutePokeyChannels(0);
			this.NextPlayerCycle = 0;
		}

		/**
		/// Returns current playback position in blocks.
		/// A block is one sample or a pair of samples for stereo.
		 */
		public int GetBlocksPlayed()
		{
			return this.BlocksPlayed;
		}

		/**
		/// Returns current playback position in milliseconds.
		 */
		public int GetPosition()
		{
			return this.BlocksPlayed * 10 / 441;
		}

		static int MillisecondsToBlocks(int milliseconds)
		{
			return milliseconds * 441 / 10;
		}

		/**
		/// Changes the playback position.
		 * @param block The requested absolute position in samples (always 44100 per second, even in stereo).
		 */
		public void SeekSample(int block)
		{
			if (block < this.BlocksPlayed)
				PlaySong(this.CurrentSong, this.CurrentDuration);
			while (this.BlocksPlayed + this.Pokeys.ReadySamplesEnd < block) {
				this.BlocksPlayed += this.Pokeys.ReadySamplesEnd;
				DoFrame();
			}
			this.Pokeys.ReadySamplesStart = block - this.BlocksPlayed;
			this.BlocksPlayed = block;
		}

		/**
		/// Changes the playback position.
		 * @param position The requested absolute position in milliseconds.
		 */
		public void Seek(int position)
		{
			SeekSample(MillisecondsToBlocks(position));
		}

		static void PutLittleEndian(byte[] buffer, int offset, int value)
		{
			buffer[offset] = (byte) value;
			buffer[offset + 1] = (byte) (value >> 8);
			buffer[offset + 2] = (byte) (value >> 16);
			buffer[offset + 3] = (byte) (value >> 24);
		}

		static int FourCC(string s)
		{
			return (s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24) & 2147483647;
		}

		static void PutLittleEndians(byte[] buffer, int offset, int value1, int value2)
		{
			PutLittleEndian(buffer, offset, value1);
			PutLittleEndian(buffer, offset + 4, value2);
		}

		static int PutWavMetadata(byte[] buffer, int offset, int fourCC, string value)
		{
			int len = value.Length;
			if (len > 0) {
				PutLittleEndians(buffer, offset, fourCC, (len | 1) + 1);
				offset += 8;
				for (int i = 0; i < len; i++)
					buffer[offset++] = (byte) value[i];
				buffer[offset++] = 0;
				if ((len & 1) == 0)
					buffer[offset++] = 0;
			}
			return offset;
		}

		/**
		/// Fills leading bytes of the specified buffer with WAV file header.
		/// Returns the number of changed bytes.
		 * @param buffer The destination buffer.
		 * @param format Format of samples.
		 * @param metadata Include metadata (title, author, date).
		 */
		public int GetWavHeader(byte[] buffer, ASAPSampleFormat format, bool metadata, int soundbuffersize)
		{
			int use16bit = format != ASAPSampleFormat.U8 ? 1 : 0;
			int blockSize = this.ModuleInfo.GetChannels() << use16bit;
			int bytesPerSecond = 44100 * blockSize;
			int totalBlocks = MillisecondsToBlocks(this.CurrentDuration);
			int nBytes = soundbuffersize; //(totalBlocks - this.BlocksPlayed) * blockSize;
			PutLittleEndian(buffer, 8, 1163280727);
			PutLittleEndians(buffer, 12, 544501094, 16);
			buffer[20] = 1;
			buffer[21] = 0;
			buffer[22] = (byte) this.ModuleInfo.GetChannels();
			buffer[23] = 0;
			PutLittleEndians(buffer, 24, 44100, bytesPerSecond);
			buffer[32] = (byte) blockSize;
			buffer[33] = 0;
			buffer[34] = (byte) (8 << use16bit);
			buffer[35] = 0;
			int i = 36;
			/*
			if (metadata) {
				int year = this.ModuleInfo.GetYear();
				if (this.ModuleInfo.GetTitle().Length > 0 || this.ModuleInfo.GetAuthor().Length > 0 || year > 0) {
					PutLittleEndian(buffer, 44, 1330007625);
					i = PutWavMetadata(buffer, 48, 1296125513, this.ModuleInfo.GetTitle());
					i = PutWavMetadata(buffer, i, 1414676809, this.ModuleInfo.GetAuthor());
					if (year > 0) {
						PutLittleEndians(buffer, i, 1146241865, 6);
						for (int j = 3; j >= 0; j--) {
							buffer[i + 8 + j] = (byte) (48 + year % 10);
							year /= 10;
						}
						buffer[i + 12] = 0;
						buffer[i + 13] = 0;
						i += 14;
					}
					PutLittleEndians(buffer, 36, 1414744396, i - 44);
				}
			}*/
			PutLittleEndians(buffer, 0, 1179011410, i + nBytes);
			PutLittleEndians(buffer, i, 1635017060, nBytes);
			return i + 8;
		}

		int GenerateAt(byte[] buffer, int bufferOffset, int bufferLen, ASAPSampleFormat format)
		{
			if (this.SilenceCycles > 0 && this.SilenceCyclesCounter <= 0)
				return 0;
			int blockShift = this.ModuleInfo.GetChannels() - 1 + (format != ASAPSampleFormat.U8 ? 1 : 0);
			int bufferBlocks = bufferLen >> blockShift;
			if (this.CurrentDuration > 0) {
				int totalBlocks = MillisecondsToBlocks(this.CurrentDuration);
				if (bufferBlocks > totalBlocks - this.BlocksPlayed)
					bufferBlocks = totalBlocks - this.BlocksPlayed;
			}
			int block = 0;
			for (;;) {
				int blocks = this.Pokeys.Generate(buffer, bufferOffset + (block << blockShift), bufferBlocks - block, format);
				this.BlocksPlayed += blocks;
				block += blocks;
				if (block >= bufferBlocks)
					break;
				int cycles = DoFrame();
				if (this.SilenceCycles > 0) {
					if (this.Pokeys.IsSilent() && !this.GtiaOrCovoxPlayedThisFrame) {
						this.SilenceCyclesCounter -= cycles;
						if (this.SilenceCyclesCounter <= 0)
							break;
					}
					else
						this.SilenceCyclesCounter = this.SilenceCycles;
				}
			}
			return block << blockShift;
		}

		/**
		/// Fills the specified buffer with generated samples.
		 * @param buffer The destination buffer.
		 * @param bufferLen Number of bytes to fill.
		 * @param format Format of samples.
		 */
		public int Generate(byte[] buffer, int bufferLen, ASAPSampleFormat format)
		{
			return GenerateAt(buffer, 0, bufferLen, format);
		}

		/**
		/// Returns POKEY channel volume - an integer between 0 and 15.
		 * @param channel POKEY channel number (from 0 to 7).
		 */
		public int GetPokeyChannelVolume(int channel)
		{
			Pokey pokey = (channel & 4) == 0 ? this.Pokeys.BasePokey : this.Pokeys.ExtraPokey;
			return pokey.Channels[channel & 3].Audc & 15;
		}
	}

	static class ASAP6502
	{

		internal static byte[] GetPlayerRoutine(ASAPInfo info)
		{
			switch (info.Type) {
			case ASAPModuleType.Cmc:
				return CiResource.cmc_obx;
			case ASAPModuleType.Cms:
				// Embedded CMS player (the upstream ASAP cms.obx was missing from this port)
				return CmsPlayerResource.cms_obx;
			default:
				return null;
			}
		}
	}

	class DurationParser
	{

		string Source;

		int Position;

		int Length;

		int ParseDigit(int max)
		{
			if (this.Position >= this.Length)
				throw new Exception("Invalid duration");
			int digit = this.Source[this.Position++] - 48;
			if (digit < 0 || digit > max)
				throw new Exception("Invalid duration");
			return digit;
		}

		internal int Parse(string s)
		{
			this.Source = s;
			this.Position = 0;
			this.Length = s.Length;
			int result = ParseDigit(9);
			int digit;
			if (this.Position < this.Length) {
				digit = s[this.Position] - 48;
				if (digit >= 0 && digit <= 9) {
					this.Position++;
					result = result * 10 + digit;
				}
				if (this.Position < this.Length && s[this.Position] == ':') {
					this.Position++;
					digit = ParseDigit(5);
					result = result * 60 + digit * 10;
					digit = ParseDigit(9);
					result += digit;
				}
			}
			result *= 1000;
			if (this.Position >= this.Length)
				return result;
			if (s[this.Position++] != '.')
				throw new Exception("Invalid duration");
			digit = ParseDigit(9);
			result += digit * 100;
			if (this.Position >= this.Length)
				return result;
			digit = ParseDigit(9);
			result += digit * 10;
			if (this.Position >= this.Length)
				return result;
			digit = ParseDigit(9);
			result += digit;
			return result;
		}
	}

	/// <summary>Information about a music file.</summary>
	public class ASAPInfo
	{
		public ASAPInfo()
		{
		}

		/// <summary>ASAP version - major part.</summary>
		public const int VersionMajor = 5;

		/// <summary>ASAP version - minor part.</summary>
		public const int VersionMinor = 0;

		/// <summary>ASAP version - micro part.</summary>
		public const int VersionMicro = 1;

		/// <summary>ASAP version as a string.</summary>
		public const string Version = "5.0.1";

		/// <summary>Years ASAP was created in.</summary>
		public const string Years = "2005-2020";

		/// <summary>Short credits for ASAP.</summary>
		public const string Credits = "Another Slight Atari Player (C) 2005-2020 Piotr Fusik\nCMC, MPT, TMC, TM2 players (C) 1994-2005 Marcin Lewandowski\nRMT player (C) 2002-2005 Radek Sterba\nDLT player (C) 2009 Marek Konopka\nCMS player (C) 1999 David Spilka\nFC player (C) 2011 Jerzy Kut\n";

		/// <summary>Short license notice.</summary>
		/// <remarks>Display after the credits.</remarks>
		public const string Copyright = "This program is free software; you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation; either version 2 of the License, or (at your option) any later version.";

		/// <summary>Maximum length of a supported input file.</summary>
		/// <remarks>You may assume that files longer than this are not supported by ASAP.</remarks>
		public const int MaxModuleLength = 65000;

		/// <summary>Maximum length of text metadata.</summary>
		public const int MaxTextLength = 127;

		/// <summary>Maximum number of songs in a file.</summary>
		public const int MaxSongs = 32;

		const int SeenThisCall = 1;

		const int SeenBefore = 2;

		const int SeenRepeat = 3;

		const int CmrBassTableOffset = 1807;

		const int RmtInit = 3200;

		string Filename;

		string Author;

		string Title;

		string Date;

		int Channels;

		int Songs;

		int DefaultSong;

		readonly int[] Durations = new int[32];

		readonly bool[] Loops = new bool[32];

		bool Ntsc;

		internal ASAPModuleType Type;

		int Fastplay;

		int Music;

		int Init;

		internal int Player;

		int CovoxAddr;

		internal int HeaderLen;

		internal readonly byte[] SongPos = new byte[32];

		static bool IsValidChar(int c)
		{
			return c >= 32 && c <= 124 && c != 96 && c != 123;
		}

		static void CheckValidChar(int c)
		{
			if (!IsValidChar(c))
				throw new Exception("Invalid character");
		}

		static void CheckValidText(string s)
		{
			int n = s.Length;
			if (n > 127)
				throw new Exception("Text too long");
			for (int i = 0; i < n; i++)
				CheckValidChar(s[i]);
		}

		/**
		/// Returns author's name.
		/// A nickname may be included in parentheses after the real name.
		/// Multiple authors are separated with <c>" &amp; "</c>.
		/// An empty string means the author is unknown.
		 */
		public string GetAuthor()
		{
			return this.Author;
		}

		/**
		/// Sets author's name.
		/// A nickname may be included in parentheses after the real name.
		/// Multiple authors are separated with <c>" &amp; "</c>.
		/// An empty string means the author is unknown.
		 * @param value New author's name for the current music.
		 */
		public void SetAuthor(string value)
		{
			CheckValidText(value);
			this.Author = value;
		}

		/**
		/// Returns music title.
		/// An empty string means the title is unknown.
		 */
		public string GetTitle()
		{
			return this.Title;
		}

		/**
		/// Sets music title.
		/// An empty string means the title is unknown.
		 * @param value New title for the current music.
		 */
		public void SetTitle(string value)
		{
			CheckValidText(value);
			this.Title = value;
		}

		/**
		/// Returns music title or filename.
		/// If title is unknown returns filename without the path or extension.
		 */
		public string GetTitleOrFilename()
		{
			return this.Title.Length > 0 ? this.Title : this.Filename;
		}

		/**
		/// Returns music creation date.
		/// 
		/// <para>Some of the possible formats are:</para>
		/// <list type="bullet">
		/// <item>YYYY</item>
		/// <item>MM/YYYY</item>
		/// <item>DD/MM/YYYY</item>
		/// <item>YYYY-YYYY</item>
		/// </list>
		/// <para>An empty string means the date is unknown.</para>
		 */
		public string GetDate()
		{
			return this.Date;
		}

		/**
		/// Sets music creation date.
		/// 
		/// <para>Some of the possible formats are:</para>
		/// <list type="bullet">
		/// <item>YYYY</item>
		/// <item>MM/YYYY</item>
		/// <item>DD/MM/YYYY</item>
		/// <item>YYYY-YYYY</item>
		/// </list>
		/// <para>An empty string means the date is unknown.</para>
		 * @param value New music creation date.
		 */
		public void SetDate(string value)
		{
			CheckValidText(value);
			this.Date = value;
		}

		
		int CheckDate()
		{
			int n = this.Date.Length;
			switch (n) {
			case 4:
			case 7:
			case 10:
				break;
			default:
				return -1;
			}
			for (int i = 0; i < n; i++) {
				int c = this.Date[i];
				if (i == n - 5 || i == n - 8) {
					if (c != 47)
						return -1;
				}
				else if (c < 48 || c > 57)
					return -1;
			}
			return n;
		}

		int GetTwoDateDigits(int i)
		{
			return (this.Date[i] - 48) * 10 + this.Date[i + 1] - 48;
		}

		/**
		/// Returns music creation year.
		/// -1 means the year is unknown.
		 */
		public int GetYear()
		{
			int n = CheckDate();
			if (n < 0)
				return -1;
			return GetTwoDateDigits(n - 4) * 100 + GetTwoDateDigits(n - 2);
		}

		/**
		/// Returns music creation month (1-12).
		/// -1 means the month is unknown.
		 */
		public int GetMonth()
		{
			int n = CheckDate();
			if (n < 7)
				return -1;
			return GetTwoDateDigits(n - 7);
		}

		/**
		/// Returns day of month of the music creation date.
		/// -1 means the day is unknown.
		 */
		public int GetDayOfMonth()
		{
			int n = CheckDate();
			if (n != 10)
				return -1;
			return GetTwoDateDigits(0);
		}

		/**
		/// Returns 1 for mono or 2 for stereo.
		 */
		public int GetChannels()
		{
			return this.Channels;
		}

		/**
		/// Returns number of songs in the file.
		 */
		public int GetSongs()
		{
			return this.Songs;
		}

		/**
		/// Returns 0-based index of the "main" song.
		/// The specified song should be played by default.
		 */
		public int GetDefaultSong()
		{
			return this.DefaultSong;
		}

		/**
		/// Sets the 0-based index of the "main" song.
		 * @param song New default song.
		 */
		public void SetDefaultSong(int song)
		{
			if (song < 0 || song >= this.Songs)
				throw new Exception("Song out of range");
			this.DefaultSong = song;
		}

		/**
		/// Returns length of the specified song.
		/// The length is specified in milliseconds. -1 means the length is indeterminate.
		 * @param song Song to get length of, 0-based.
		 */
		public int GetDuration(int song)
		{
			return this.Durations[song];
		}

		/**
		/// Sets length of the specified song.
		/// The length is specified in milliseconds. -1 means the length is indeterminate.
		 * @param song Song to set length of, 0-based.
		 * @param duration New length in milliseconds.
		 */
		public void SetDuration(int song, int duration)
		{
			if (song < 0 || song >= this.Songs)
				throw new Exception("Song out of range");
			this.Durations[song] = duration;
		}

		/**
		/// Returns information whether the specified song loops.
		/// 
		/// <para>Returns:</para>
		/// <list type="bullet">
		/// <item><see langword="true" /> if the song loops</item>
		/// <item><see langword="false" /> if the song stops</item>
		/// </list>
		 * @param song Song to check for looping, 0-based.
		 */
		public bool GetLoop(int song)
		{
			return this.Loops[song];
		}

		/**
		/// Sets information whether the specified song loops.
		/// 
		/// <para>Use:</para>
		/// <list type="bullet">
		/// <item><see langword="true" /> if the song loops</item>
		/// <item><see langword="false" /> if the song stops</item>
		/// </list>
		 * @param song Song to set as looping, 0-based.
		 * @param loop <see langword="true" /> if the song loops.
		 */
		public void SetLoop(int song, bool loop)
		{
			if (song < 0 || song >= this.Songs)
				throw new Exception("Song out of range");
			this.Loops[song] = loop;
		}

		/**
		/// Returns <see langword="true" /> for NTSC song and <see langword="false" /> for PAL song.
		 */
		public bool IsNtsc()
		{
			return this.Ntsc;
		}

		/**
		/// Returns the letter argument for the TYPE SAP tag.
		/// Returns zero for non-SAP files.
		 */
		public int GetTypeLetter()
		{
			switch (this.Type) {
			case ASAPModuleType.SapB:
				return 66;
			case ASAPModuleType.SapC:
				return 67;
			case ASAPModuleType.SapD:
				return 68;
			case ASAPModuleType.SapS:
				return 83;
			default:
				return 0;
			}
		}

		/**
		/// Returns player routine rate in Atari scanlines.
		 */
		public int GetPlayerRateScanlines()
		{
			return this.Fastplay;
		}

		/**
		/// Returns approximate player routine rate in Hz.
		 */
		public int GetPlayerRateHz()
		{
			int scanlineClock = this.Ntsc ? 15699 : 15556;
			return (scanlineClock + (this.Fastplay >> 1)) / this.Fastplay;
		}

		/**
		/// Returns the address of the module.
		/// Returns -1 if unknown.
		 */
		public int GetMusicAddress()
		{
			return this.Music;
		}

		/**
		/// Causes music to be relocated.
		/// Use only with <c>ASAPWriter.Write</c>.
		 * @param address New music address.
		 */
		public void SetMusicAddress(int address)
		{
			if (address < 0 || address >= 65535)
				throw new Exception("Invalid music address");
			this.Music = address;
		}

		/**
		/// Returns the address of the player initialization routine.
		/// Returns -1 if no initialization routine.
		 */
		public int GetInitAddress()
		{
			return this.Init;
		}

		/**
		/// Returns the address of the player routine.
		 */
		public int GetPlayerAddress()
		{
			return this.Player;
		}

		/**
		/// Returns the address of the COVOX chip.
		/// Returns -1 if no COVOX enabled.
		 */
		public int GetCovoxAddress()
		{
			return this.CovoxAddr;
		}

		/**
		/// Retturns the length of the SAP header in bytes.
		 */
		public int GetSapHeaderLength()
		{
			return this.HeaderLen;
		}

		/**
		/// Returns the offset of instrument names for RMT module.
		/// Returns -1 if not an RMT module or RMT module without instrument names.
		 * @param module Content of the RMT file.
		 * @param moduleLen Length of the RMT file.
		 */
		public int GetInstrumentNamesOffset(byte[] module, int moduleLen)
		{
			if (this.Type != ASAPModuleType.Rmt)
				return -1;
			for (int offset = GetWord(module, 4) - GetWord(module, 2) + 12; offset < moduleLen; offset++) {
				if (module[offset - 1] == 0)
					return offset;
			}
			return -1;
		}

		internal static int GetWord(byte[] array, int i)
		{
			return array[i] + (array[i + 1] << 8);
		}

		void ParseModule(byte[] module, int moduleLen)
		{
			if ((module[0] != 255 || module[1] != 255) && (module[0] != 0 || module[1] != 0))
				throw new Exception("Invalid two leading bytes of the module");
			this.Music = GetWord(module, 2);
			int musicLastByte = GetWord(module, 4);
			if (this.Music <= 55295 && musicLastByte >= 53248)
				throw new Exception("Module address conflicts with hardware registers");
			int blockLen = musicLastByte + 1 - this.Music;
			if (6 + blockLen != moduleLen) {
				if (this.Type != ASAPModuleType.Rmt || 11 + blockLen > moduleLen)
					throw new Exception("Module length doesn't match headers");
				int infoAddr = GetWord(module, 6 + blockLen);
				if (infoAddr != this.Music + blockLen)
					throw new Exception("Invalid address of RMT info");
				int infoLen = GetWord(module, 8 + blockLen) + 1 - infoAddr;
				if (10 + blockLen + infoLen != moduleLen)
					throw new Exception("Invalid RMT info block");
			}
		}

		void AddSong(int playerCalls)
		{
			long scanlines = playerCalls * this.Fastplay;
			this.Durations[this.Songs++] = (int) (scanlines * 38000 / 591149);
		}

		void ParseCmcSong(byte[] module, int pos)
		{
			int tempo = module[25];
			int playerCalls = 0;
			int repStartPos = 0;
			int repEndPos = 0;
			int repTimes = 0;
			byte[] seen = new byte[85];
			while (pos >= 0 && pos < 85) {
				if (pos == repEndPos && repTimes > 0) {
					for (int i = 0; i < 85; i++)
						if (seen[i] == 1 || seen[i] == 3)
							seen[i] = 0;
					repTimes--;
					pos = repStartPos;
				}
				if (seen[pos] != 0) {
					if (seen[pos] != 1)
						this.Loops[this.Songs] = true;
					break;
				}
				seen[pos] = 1;
				int p1 = module[518 + pos];
				int p2 = module[603 + pos];
				int p3 = module[688 + pos];
				if (p1 == 254 || p2 == 254 || p3 == 254) {
					pos++;
					continue;
				}
				p1 |= this.Type == ASAPModuleType.Cms ? 7 : 15;
				switch (p1) {
				case 135:
				case 167:
					pos++;
					break;
				case 143:
					pos = -1;
					break;
				case 151:
					if (p2 < 128) {
						playerCalls += p2;
						if (p3 < 128)
							playerCalls += p3 * 50;
					}
					pos++;
					break;
				case 159:
					pos = p2;
					break;
				case 175:
					pos -= p2;
					break;
				case 191:
					pos += p2;
					break;
				case 207:
					if (p2 < 128) {
						tempo = p2;
						pos++;
					}
					else
						pos = -1;
					break;
				case 223:
					pos++;
					repStartPos = pos;
					repEndPos = pos + p2;
					repTimes = p3 - 1;
					break;
				case 239:
					this.Loops[this.Songs] = true;
					pos = -1;
					break;
				default:
					p2 = repTimes > 0 ? 3 : 2;
					for (p1 = 0; p1 < 85; p1++)
						if (seen[p1] == 1)
							seen[p1] = (byte) p2;
					playerCalls += tempo * (this.Type == ASAPModuleType.Cm3 ? 48 : 64);
					pos++;
					break;
				}
			}
			AddSong(playerCalls);
		}

		void ParseCmc(byte[] module, int moduleLen, ASAPModuleType type)
		{
			if (moduleLen < 774)
				throw new Exception("Module too short");
			this.Type = type;
			ParseModule(module, moduleLen);
			int lastPos = 84;
			while (--lastPos >= 0) {
				if (module[518 + lastPos] < 176 || module[603 + lastPos] < 64 || module[688 + lastPos] < 64)
					break;
				if (this.Channels == 2) {
					if (module[774 + lastPos] < 176 || module[859 + lastPos] < 64 || module[944 + lastPos] < 64)
						break;
				}
			}
			this.Songs = 0;
			ParseCmcSong(module, 0);
			for (int pos = 0; pos < lastPos && this.Songs < 32; pos++)
				if (module[518 + pos] == 143 || module[518 + pos] == 239)
					ParseCmcSong(module, pos + 1);
		}

		static bool IsDltTrackEmpty(byte[] module, int pos)
		{
			return module[8198 + pos] >= 67 && module[8454 + pos] >= 64 && module[8710 + pos] >= 64 && module[8966 + pos] >= 64;
		}

		static bool IsDltPatternEnd(byte[] module, int pos, int i)
		{
			for (int ch = 0; ch < 4; ch++) {
				int pattern = module[8198 + (ch << 8) + pos];
				if (pattern < 64) {
					int offset = 6 + (pattern << 7) + (i << 1);
					if ((module[offset] & 128) == 0 && (module[offset + 1] & 128) != 0)
						return true;
				}
			}
			return false;
		}

		void ParseDltSong(byte[] module, bool[] seen, int pos)
		{
			while (pos < 128 && !seen[pos] && IsDltTrackEmpty(module, pos))
				seen[pos++] = true;
			this.SongPos[this.Songs] = (byte) pos;
			int playerCalls = 0;
			bool loop = false;
			int tempo = 6;
			while (pos < 128) {
				if (seen[pos]) {
					loop = true;
					break;
				}
				seen[pos] = true;
				int p1 = module[8198 + pos];
				if (p1 == 64 || IsDltTrackEmpty(module, pos))
					break;
				if (p1 == 65)
					pos = module[8326 + pos];
				else if (p1 == 66)
					tempo = module[8326 + pos++];
				else {
					for (int i = 0; i < 64 && !IsDltPatternEnd(module, pos, i); i++)
						playerCalls += tempo;
					pos++;
				}
			}
			if (playerCalls > 0) {
				this.Loops[this.Songs] = loop;
				AddSong(playerCalls);
			}
		}

		void ParseDlt(byte[] module, int moduleLen)
		{
			if (moduleLen != 11270 && moduleLen != 11271)
				throw new Exception("Invalid module length");
			this.Type = ASAPModuleType.Dlt;
			ParseModule(module, moduleLen);
			if (this.Music != 8192)
				throw new Exception("Unsupported module address");
			bool[] seen = new bool[128];
			this.Songs = 0;
			for (int pos = 0; pos < 128 && this.Songs < 32; pos++) {
				if (!seen[pos])
					ParseDltSong(module, seen, pos);
			}
			if (this.Songs == 0)
				throw new Exception("No songs found");
		}

		void ParseMptSong(byte[] module, bool[] globalSeen, int songLen, int pos)
		{
			int addrToOffset = GetWord(module, 2) - 6;
			int tempo = module[463];
			int playerCalls = 0;
			byte[] seen = new byte[256];
			int[] patternOffset = new int[4];
			int[] blankRows = new int[4];
			int[] blankRowsCounter = new int[4];
			while (pos < songLen) {
				if (seen[pos] != 0) {
					if (seen[pos] != 1)
						this.Loops[this.Songs] = true;
					break;
				}
				seen[pos] = 1;
				globalSeen[pos] = true;
				int i = module[464 + pos * 2];
				if (i == 255) {
					pos = module[465 + pos * 2];
					continue;
				}
				int ch;
				for (ch = 3; ch >= 0; ch--) {
					i = module[454 + ch] + (module[458 + ch] << 8) - addrToOffset;
					i = module[i + pos * 2];
					if (i >= 64)
						break;
					i <<= 1;
					i = GetWord(module, 70 + i);
					patternOffset[ch] = i == 0 ? 0 : i - addrToOffset;
					blankRowsCounter[ch] = 0;
				}
				if (ch >= 0)
					break;
				for (i = 0; i < songLen; i++)
					if (seen[i] == 1)
						seen[i] = 2;
				for (int patternRows = module[462]; --patternRows >= 0;) {
					for (ch = 3; ch >= 0; ch--) {
						if (patternOffset[ch] == 0)
							continue;
						if (--blankRowsCounter[ch] >= 0)
							continue;
						for (;;) {
							i = module[patternOffset[ch]++];
							if (i < 64 || i == 254)
								break;
							if (i < 128)
								continue;
							if (i < 192) {
								blankRows[ch] = i - 128;
								continue;
							}
							if (i < 208)
								continue;
							if (i < 224) {
								tempo = i - 207;
								continue;
							}
							patternRows = 0;
						}
						blankRowsCounter[ch] = blankRows[ch];
					}
					playerCalls += tempo;
				}
				pos++;
			}
			if (playerCalls > 0)
				AddSong(playerCalls);
		}

		void ParseMpt(byte[] module, int moduleLen)
		{
			if (moduleLen < 464)
				throw new Exception("Module too short");
			this.Type = ASAPModuleType.Mpt;
			ParseModule(module, moduleLen);
			int track0Addr = GetWord(module, 2) + 458;
			if (module[454] + (module[458] << 8) != track0Addr)
				throw new Exception("Invalid address of the first track");
			int songLen = (module[455] + (module[459] << 8) - track0Addr) >> 1;
			if (songLen > 254)
				throw new Exception("Song too long");
			bool[] globalSeen = new bool[256];
			this.Songs = 0;
			for (int pos = 0; pos < songLen && this.Songs < 32; pos++) {
				if (!globalSeen[pos]) {
					this.SongPos[this.Songs] = (byte) pos;
					ParseMptSong(module, globalSeen, songLen, pos);
				}
			}
			if (this.Songs == 0)
				throw new Exception("No songs found");
		}

		static int GetRmtInstrumentFrames(byte[] module, int instrument, int volume, int volumeFrame, bool onExtraPokey)
		{
			int addrToOffset = GetWord(module, 2) - 6;
			instrument = GetWord(module, 14) - addrToOffset + (instrument << 1);
			if (module[instrument + 1] == 0)
				return 0;
			instrument = GetWord(module, instrument) - addrToOffset;
			int perFrame = module[12];
			int playerCall = volumeFrame * perFrame;
			int playerCalls = playerCall;
			int index = module[instrument] + 1 + playerCall * 3;
			int indexEnd = module[instrument + 2] + 3;
			int indexLoop = module[instrument + 3];
			if (indexLoop >= indexEnd)
				return 0;
			int volumeSlideDepth = module[instrument + 6];
			int volumeMin = module[instrument + 7];
			if (index >= indexEnd)
				index = (index - indexEnd) % (indexEnd - indexLoop) + indexLoop;
			else {
				do {
					int vol = module[instrument + index];
					if (onExtraPokey)
						vol >>= 4;
					if ((vol & 15) >= GetRmtInstrumentFramesrmtVolumeSilent[volume])
						playerCalls = playerCall + 1;
					playerCall++;
					index += 3;
				}
				while (index < indexEnd);
			}
			if (volumeSlideDepth == 0)
				return playerCalls / perFrame;
			int volumeSlide = 128;
			bool silentLoop = false;
			for (;;) {
				if (index >= indexEnd) {
					if (silentLoop)
						break;
					silentLoop = true;
					index = indexLoop;
				}
				int vol = module[instrument + index];
				if (onExtraPokey)
					vol >>= 4;
				if ((vol & 15) >= GetRmtInstrumentFramesrmtVolumeSilent[volume]) {
					playerCalls = playerCall + 1;
					silentLoop = false;
				}
				playerCall++;
				index += 3;
				volumeSlide -= volumeSlideDepth;
				if (volumeSlide < 0) {
					volumeSlide += 256;
					if (--volume <= volumeMin)
						break;
				}
			}
			return playerCalls / perFrame;
		}

		void ParseRmtSong(byte[] module, bool[] globalSeen, int songLen, int posShift, int pos)
		{
			int addrToOffset = GetWord(module, 2) - 6;
			int tempo = module[11];
			int frames = 0;
			int songOffset = GetWord(module, 20) - addrToOffset;
			int patternLoOffset = GetWord(module, 16) - addrToOffset;
			int patternHiOffset = GetWord(module, 18) - addrToOffset;
			byte[] seen = new byte[256];
			int[] patternBegin = new int[8];
			int[] patternOffset = new int[8];
			int[] blankRows = new int[8];
			int[] instrumentNo = new int[8];
			int[] instrumentFrame = new int[8];
			int[] volumeValue = new int[8];
			int[] volumeFrame = new int[8];
			while (pos < songLen) {
				if (seen[pos] != 0) {
					if (seen[pos] != 1)
						this.Loops[this.Songs] = true;
					break;
				}
				seen[pos] = 1;
				globalSeen[pos] = true;
				if (module[songOffset + (pos << posShift)] == 254) {
					pos = module[songOffset + (pos << posShift) + 1];
					continue;
				}
				for (int ch = 0; ch < 1 << posShift; ch++) {
					int p = module[songOffset + (pos << posShift) + ch];
					if (p == 255)
						blankRows[ch] = 256;
					else {
						patternOffset[ch] = patternBegin[ch] = module[patternLoOffset + p] + (module[patternHiOffset + p] << 8) - addrToOffset;
						if (patternOffset[ch] < 0)
							return;
						blankRows[ch] = 0;
					}
				}
				for (int i = 0; i < songLen; i++)
					if (seen[i] == 1)
						seen[i] = 2;
				for (int patternRows = module[10]; --patternRows >= 0;) {
					for (int ch = 0; ch < 1 << posShift; ch++) {
						if (--blankRows[ch] > 0)
							continue;
						for (;;) {
							int i = module[patternOffset[ch]++];
							if ((i & 63) < 62) {
								i += module[patternOffset[ch]++] << 8;
								if ((i & 63) != 61) {
									instrumentNo[ch] = i >> 10;
									instrumentFrame[ch] = frames;
								}
								volumeValue[ch] = i >> 6 & 15;
								volumeFrame[ch] = frames;
								break;
							}
							if (i == 62) {
								blankRows[ch] = module[patternOffset[ch]++];
								break;
							}
							if ((i & 63) == 62) {
								blankRows[ch] = i >> 6;
								break;
							}
							if ((i & 191) == 63) {
								tempo = module[patternOffset[ch]++];
								continue;
							}
							if (i == 191) {
								patternOffset[ch] = patternBegin[ch] + module[patternOffset[ch]];
								continue;
							}
							patternRows = -1;
							break;
						}
						if (patternRows < 0)
							break;
					}
					if (patternRows >= 0)
						frames += tempo;
				}
				pos++;
			}
			int instrumentFrames = 0;
			for (int ch = 0; ch < 1 << posShift; ch++) {
				int frame = instrumentFrame[ch];
				frame += GetRmtInstrumentFrames(module, instrumentNo[ch], volumeValue[ch], volumeFrame[ch] - frame, ch >= 4);
				if (instrumentFrames < frame)
					instrumentFrames = frame;
			}
			if (frames > instrumentFrames) {
				if (frames - instrumentFrames > 100)
					this.Loops[this.Songs] = false;
				frames = instrumentFrames;
			}
			if (frames > 0)
				AddSong(frames);
		}

		static bool ValidateRmt(byte[] module, int moduleLen)
		{
			if (moduleLen < 48)
				return false;
			if (module[6] != 82 || module[7] != 77 || module[8] != 84 || module[13] != 1)
				return false;
			return true;
		}

		void ParseRmt(byte[] module, int moduleLen)
		{
			if (!ValidateRmt(module, moduleLen))
				throw new Exception("Invalid RMT file");
			int posShift;
			switch (module[9]) {
			case 52:
				posShift = 2;
				break;
			case 56:
				this.Channels = 2;
				posShift = 3;
				break;
			default:
				throw new Exception("Unsupported number of channels");
			}
			int perFrame = module[12];
			if (perFrame < 1 || perFrame > 4)
				throw new Exception("Unsupported player call rate");
			this.Type = ASAPModuleType.Rmt;
			ParseModule(module, moduleLen);
			int blockLen = GetWord(module, 4) + 1 - this.Music;
			int songLen = GetWord(module, 4) + 1 - GetWord(module, 20);
			if (posShift == 3 && (songLen & 4) != 0 && module[6 + blockLen - 4] == 254)
				songLen += 4;
			songLen >>= posShift;
			if (songLen >= 256)
				throw new Exception("Song too long");
			bool[] globalSeen = new bool[256];
			this.Songs = 0;
			for (int pos = 0; pos < songLen && this.Songs < 32; pos++) {
				if (!globalSeen[pos]) {
					this.SongPos[this.Songs] = (byte) pos;
					ParseRmtSong(module, globalSeen, songLen, posShift, pos);
				}
			}
			this.Fastplay = 312 / perFrame;
			this.Player = 1536;
			if (this.Songs == 0)
				throw new Exception("No songs found");
			byte[] title = new byte[127];
			int titleLen;
			for (titleLen = 0; titleLen < 127 && 10 + blockLen + titleLen < moduleLen; titleLen++) {
				int c = module[10 + blockLen + titleLen];
				if (c == 0)
					break;
				title[titleLen] = (byte) (IsValidChar(c) ? c : 32);
			}
			this.Title = Encoding.UTF8.GetString(title, 0, titleLen);
		}

		void ParseTmcSong(byte[] module, int pos)
		{
			int addrToOffset = GetWord(module, 2) - 6;
			int tempo = module[36] + 1;
			int frames = 0;
			int[] patternOffset = new int[8];
			int[] blankRows = new int[8];
			while (module[437 + pos] < 128) {
				for (int ch = 7; ch >= 0; ch--) {
					int pat = module[437 + pos - 2 * ch];
					patternOffset[ch] = module[166 + pat] + (module[294 + pat] << 8) - addrToOffset;
					blankRows[ch] = 0;
				}
				for (int patternRows = 64; --patternRows >= 0;) {
					for (int ch = 7; ch >= 0; ch--) {
						if (--blankRows[ch] >= 0)
							continue;
						for (;;) {
							int i = module[patternOffset[ch]++];
							if (i < 64) {
								patternOffset[ch]++;
								break;
							}
							if (i == 64) {
								i = module[patternOffset[ch]++];
								if ((i & 127) == 0)
									patternRows = 0;
								else
									tempo = (i & 127) + 1;
								if (i >= 128)
									patternOffset[ch]++;
								break;
							}
							if (i < 128) {
								i = module[patternOffset[ch]++] & 127;
								if (i == 0)
									patternRows = 0;
								else
									tempo = i + 1;
								patternOffset[ch]++;
								break;
							}
							if (i < 192)
								continue;
							blankRows[ch] = i - 191;
							break;
						}
					}
					frames += tempo;
				}
				pos += 16;
			}
			if (module[436 + pos] < 128)
				this.Loops[this.Songs] = true;
			AddSong(frames);
		}

		static int ParseTmcTitle(byte[] title, int titleLen, byte[] module, int moduleOffset)
		{
			int lastOffset = moduleOffset + 29;
			while (module[lastOffset] == 32) {
				if (--lastOffset < moduleOffset)
					return titleLen;
			}
			if (titleLen > 0) {
				title[titleLen++] = 32;
				title[titleLen++] = 124;
				title[titleLen++] = 32;
			}
			while (moduleOffset <= lastOffset) {
				int c = module[moduleOffset++] & 127;
				switch (c) {
				case 20:
					c = 42;
					break;
				case 1:
				case 3:
				case 5:
				case 12:
				case 14:
				case 15:
				case 19:
					c += 96;
					break;
				case 24:
				case 26:
					c = 122;
					break;
				default:
					if (!IsValidChar(c))
						c = 32;
					break;
				}
				title[titleLen++] = (byte) c;
			}
			return titleLen;
		}

		void ParseTmc(byte[] module, int moduleLen)
		{
			if (moduleLen < 464)
				throw new Exception("Module too short");
			this.Type = ASAPModuleType.Tmc;
			ParseModule(module, moduleLen);
			this.Channels = 2;
			int i = 0;
			while (module[102 + i] == 0) {
				if (++i >= 64)
					throw new Exception("No instruments");
			}
			int lastPos = (module[102 + i] << 8) + module[38 + i] - GetWord(module, 2) - 432;
			if (437 + lastPos >= moduleLen)
				throw new Exception("Module too short");
			do {
				if (lastPos <= 0)
					throw new Exception("No songs found");
				lastPos -= 16;
			}
			while (module[437 + lastPos] >= 128);
			this.Songs = 0;
			ParseTmcSong(module, 0);
			for (i = 0; i < lastPos && this.Songs < 32; i += 16)
				if (module[437 + i] >= 128)
					ParseTmcSong(module, i + 16);
			i = module[37];
			if (i < 1 || i > 4)
				throw new Exception("Unsupported player call rate");
			this.Fastplay = 312 / i;
			byte[] title = new byte[127];
			int titleLen = ParseTmcTitle(title, 0, module, 6);
			this.Title = Encoding.UTF8.GetString(title, 0, titleLen);
		}

		void ParseTm2Song(byte[] module, int pos)
		{
			int addrToOffset = GetWord(module, 2) - 6;
			int tempo = module[36] + 1;
			int playerCalls = 0;
			int[] patternOffset = new int[8];
			int[] blankRows = new int[8];
			for (;;) {
				int patternRows = module[918 + pos];
				if (patternRows == 0)
					break;
				if (patternRows >= 128) {
					this.Loops[this.Songs] = true;
					break;
				}
				for (int ch = 7; ch >= 0; ch--) {
					int pat = module[917 + pos - 2 * ch];
					patternOffset[ch] = module[262 + pat] + (module[518 + pat] << 8) - addrToOffset;
					blankRows[ch] = 0;
				}
				while (--patternRows >= 0) {
					for (int ch = 7; ch >= 0; ch--) {
						if (--blankRows[ch] >= 0)
							continue;
						for (;;) {
							int i = module[patternOffset[ch]++];
							if (i == 0) {
								patternOffset[ch]++;
								break;
							}
							if (i < 64) {
								if (module[patternOffset[ch]++] >= 128)
									patternOffset[ch]++;
								break;
							}
							if (i < 128) {
								patternOffset[ch]++;
								break;
							}
							if (i == 128) {
								blankRows[ch] = module[patternOffset[ch]++];
								break;
							}
							if (i < 192)
								break;
							if (i < 208) {
								tempo = i - 191;
								continue;
							}
							if (i < 224) {
								patternOffset[ch]++;
								break;
							}
							if (i < 240) {
								patternOffset[ch] += 2;
								break;
							}
							if (i < 255) {
								blankRows[ch] = i - 240;
								break;
							}
							blankRows[ch] = 64;
							break;
						}
					}
					playerCalls += tempo;
				}
				pos += 17;
			}
			AddSong(playerCalls);
		}

		void ParseTm2(byte[] module, int moduleLen)
		{
			if (moduleLen < 932)
				throw new Exception("Module too short");
			this.Type = ASAPModuleType.Tm2;
			ParseModule(module, moduleLen);
			int i = module[37];
			if (i < 1 || i > 4)
				throw new Exception("Unsupported player call rate");
			this.Fastplay = 312 / i;
			this.Player = 2048;
			if (module[31] != 0)
				this.Channels = 2;
			int lastPos = 65535;
			for (i = 0; i < 128; i++) {
				int instrAddr = module[134 + i] + (module[774 + i] << 8);
				if (instrAddr != 0 && instrAddr < lastPos)
					lastPos = instrAddr;
			}
			for (i = 0; i < 256; i++) {
				int patternAddr = module[262 + i] + (module[518 + i] << 8);
				if (patternAddr != 0 && patternAddr < lastPos)
					lastPos = patternAddr;
			}
			lastPos -= GetWord(module, 2) + 896;
			if (902 + lastPos >= moduleLen)
				throw new Exception("Module too short");
			int c;
			do {
				if (lastPos <= 0)
					throw new Exception("No songs found");
				lastPos -= 17;
				c = module[918 + lastPos];
			}
			while (c == 0 || c >= 128);
			this.Songs = 0;
			ParseTm2Song(module, 0);
			for (i = 0; i < lastPos && this.Songs < 32; i += 17) {
				c = module[918 + i];
				if (c == 0 || c >= 128)
					ParseTm2Song(module, i + 17);
			}
			byte[] title = new byte[127];
			int titleLen = ParseTmcTitle(title, 0, module, 39);
			titleLen = ParseTmcTitle(title, titleLen, module, 71);
			titleLen = ParseTmcTitle(title, titleLen, module, 103);
			this.Title = Encoding.UTF8.GetString(title, 0, titleLen);
		}

		static int AfterFF(byte[] module, int moduleLen, int currentOffset)
		{
			while (currentOffset < moduleLen) {
				if (module[currentOffset++] == 255)
					return currentOffset;
			}
			throw new Exception("Module too short");
		}

		static int GetFcTrackCommand(byte[] module, int[] trackPos, int n)
		{
			return module[3 + (n << 8) + trackPos[n]];
		}

		static bool IsFcSongEnd(byte[] module, int[] trackPos)
		{
			bool allLoop = true;
			for (int n = 0; n < 3; n++) {
				if (trackPos[n] >= 256)
					return true;
				switch (GetFcTrackCommand(module, trackPos, n)) {
				case 254:
					return true;
				case 255:
					break;
				default:
					allLoop = false;
					break;
				}
			}
			return allLoop;
		}

		static bool ValidateFc(byte[] module, int moduleLen)
		{
			if (moduleLen < 899)
				return false;
			if (module[0] != 38 || module[1] != 35)
				return false;
			return true;
		}

		void ParseFc(byte[] module, int moduleLen)
		{
			if (!ValidateFc(module, moduleLen))
				throw new Exception("Invalid FC file");
			this.Type = ASAPModuleType.Fc;
			this.Player = 1024;
			this.Music = 2560;
			this.Songs = 0;
			this.HeaderLen = -1;
			int[] patternOffsets = new int[64];
			int currentOffset = 899;
			for (int i = 0; i < 64; i++) {
				patternOffsets[i] = currentOffset;
				currentOffset = AfterFF(module, moduleLen, currentOffset);
			}
			for (int i = 0; i < 32; i++)
				currentOffset = AfterFF(module, moduleLen, currentOffset);
			for (int pos = 0; pos < 256 && this.Songs < 32;) {
				int[] trackPos = new int[3];
				for (int n = 0; n < 3; n++)
					trackPos[n] = pos;
				int[] patternDelay = new int[3];
				int[] noteDuration = new int[3];
				int[] patternPos = new int[3];
				int playerCalls = 0;
				this.Loops[this.Songs] = true;
				while (!IsFcSongEnd(module, trackPos)) {
					for (int n = 0; n < 3; n++) {
						if (GetFcTrackCommand(module, trackPos, n) == 255)
							continue;
						if (patternDelay[n]-- > 0)
							continue;
						while (trackPos[n] < 256) {
							int trackCmd = GetFcTrackCommand(module, trackPos, n);
							if (trackCmd < 64) {
								int patternCmd = module[patternOffsets[trackCmd] + patternPos[n]++];
								if (patternCmd < 64) {
									patternDelay[n] = noteDuration[n];
									break;
								}
								else if (patternCmd < 96)
									noteDuration[n] = patternCmd - 64;
								else if (patternCmd == 255) {
									patternDelay[n] = 0;
									noteDuration[n] = 0;
									patternPos[n] = 0;
									trackPos[n]++;
								}
							}
							else if (trackCmd == 64)
								trackPos[n] += 2;
							else if (trackCmd == 254) {
								this.Loops[this.Songs] = false;
								break;
							}
							else if (trackCmd == 255)
								break;
							else
								trackPos[n]++;
						}
					}
					if (IsFcSongEnd(module, trackPos))
						break;
					playerCalls += module[2];
				}
				pos = -1;
				for (int n = 0; n < 3; n++) {
					int nxtrkpos = trackPos[n];
					if (patternPos[n] > 0)
						nxtrkpos++;
					if (pos < nxtrkpos)
						pos = nxtrkpos;
				}
				pos++;
				if (pos <= 256)
					AddSong(playerCalls);
			}
		}

		static bool HasStringAt(byte[] module, int moduleIndex, string s)
		{
			int n = s.Length;
			for (int i = 0; i < n; i++)
				if (module[moduleIndex + i] != s[i])
					return false;
			return true;
		}

		static int ParseDec(string s, int minVal, int maxVal)
		{
			int r = 0;
			int len = s.Length;
			for (int i = 0; i < len; i++) {
				int c = s[i];
				if (c < 48 || c > 57)
					throw new Exception("Invalid number");
				r = r * 10 + c - 48;
				if (r > maxVal)
					throw new Exception("Number too big");
			}
			if (r < minVal)
				throw new Exception("Number too small");
			return r;
		}

		static int ParseHex(string s)
		{
			int r = 0;
			int len = s.Length;
			for (int i = 0; i < len; i++) {
				if (r > 4095)
					throw new Exception("Number too big");
				int c = s[i];
				r <<= 4;
				if (c >= 48 && c <= 57)
					r += c - 48;
				else if (c >= 65 && c <= 70)
					r += c - 65 + 10;
				else if (c >= 97 && c <= 102)
					r += c - 97 + 10;
				else
					throw new Exception("Invalid number");
			}
			return r;
		}

		/**
		/// Returns the number of milliseconds represented by the given string.
		 * @param s Time in the <c>"mm:ss.xxx"</c> format.
		 */
		public static int ParseDuration(string s)
		{
			DurationParser parser = new DurationParser();
			return parser.Parse(s);
		}

		static bool ValidateSap(byte[] module, int moduleLen)
		{
			return moduleLen >= 30 && HasStringAt(module, 0, "SAP\r\n");
		}

		void ParseSap(byte[] module, int moduleLen)
		{
			if (!ValidateSap(module, moduleLen))
				throw new Exception("Invalid SAP file");
			this.Fastplay = -1;
			int type = 0;
			int moduleIndex = 5;
			int durationIndex = 0;
			while (module[moduleIndex] != 255) {
				int lineStart = moduleIndex;
				while (module[moduleIndex] > 32) {
					if (++moduleIndex >= moduleLen)
						throw new Exception("Invalid SAP file");
				}
				int tagLen = moduleIndex - lineStart;
				int argStart = -1;
				int argLen = -1;
				for (;;) {
					int c = module[moduleIndex];
					if (c > 32) {
						CheckValidChar(c);
						if (argStart < 0)
							argStart = moduleIndex;
						argLen = -1;
					}
					else {
						if (argLen < 0)
							argLen = moduleIndex - argStart;
						if (c == 10)
							break;
					}
					if (++moduleIndex >= moduleLen)
						throw new Exception("Invalid SAP file");
				}
				if (++moduleIndex + 6 >= moduleLen)
					throw new Exception("Invalid SAP file");
				if (tagLen <= 8) {
					string tag = Encoding.UTF8.GetString(module, lineStart, tagLen);
					if (argStart >= 0 && argLen <= 129) {
						string arg = Encoding.UTF8.GetString(module, argStart, argLen);
						if (argLen >= 3 && arg[0] == '\"' && arg[argLen - 1] == '\"' && arg != "\"<?>\"") {
							if (tag == "AUTHOR")
								this.Author = arg.Substring(1, argLen - 2);
							else if (tag == "NAME")
								this.Title = arg.Substring(1, argLen - 2);
							else if (tag == "DATE")
								this.Date = arg.Substring(1, argLen - 2);
						}
						else if (tag == "SONGS")
							this.Songs = ParseDec(arg, 1, 32);
						else if (tag == "DEFSONG")
							this.DefaultSong = ParseDec(arg, 0, 31);
						else if (tag == "TIME") {
							if (durationIndex >= 32)
								throw new Exception("Too many TIME tags");
							if (argLen > 5 && HasStringAt(module, argStart + argLen - 5, " LOOP")) {
								this.Loops[durationIndex] = true;
								arg = arg.Substring(0, argLen - 5);
							}
							this.Durations[durationIndex++] = ParseDuration(arg);
						}
						else if (tag == "TYPE")
							type = arg[0];
						else if (tag == "FASTPLAY")
							this.Fastplay = ParseDec(arg, 1, 32767);
						else if (tag == "MUSIC")
							this.Music = ParseHex(arg);
						else if (tag == "INIT")
							this.Init = ParseHex(arg);
						else if (tag == "PLAYER")
							this.Player = ParseHex(arg);
						else if (tag == "COVOX") {
							this.CovoxAddr = ParseHex(arg);
							if (this.CovoxAddr != 54784)
								throw new Exception("COVOX should be D600");
							this.Channels = 2;
						}
					}
					else if (tag == "STEREO")
						this.Channels = 2;
					else if (tag == "NTSC")
						this.Ntsc = true;
				}
			}
			if (this.DefaultSong >= this.Songs)
				throw new Exception("DEFSONG too big");
			switch (type) {
			case 66:
				if (this.Player < 0)
					throw new Exception("Missing PLAYER tag");
				if (this.Init < 0)
					throw new Exception("Missing INIT tag");
				this.Type = ASAPModuleType.SapB;
				break;
			case 67:
				if (this.Player < 0)
					throw new Exception("Missing PLAYER tag");
				if (this.Music < 0)
					throw new Exception("Missing MUSIC tag");
				this.Type = ASAPModuleType.SapC;
				break;
			case 68:
				if (this.Init < 0)
					throw new Exception("Missing INIT tag");
				this.Type = ASAPModuleType.SapD;
				break;
			case 83:
				if (this.Init < 0)
					throw new Exception("Missing INIT tag");
				this.Type = ASAPModuleType.SapS;
				if (this.Fastplay < 0)
					this.Fastplay = 78;
				break;
			default:
				throw new Exception("Unsupported TYPE");
			}
			if (this.Fastplay < 0)
				this.Fastplay = this.Ntsc ? 262 : 312;
			if (module[moduleIndex + 1] != 255)
				throw new Exception("Invalid binary header");
			this.HeaderLen = moduleIndex;
		}

		internal static int PackExt(string ext)
		{
			return ext.Length == 2 && ext[0] <= 'z' && ext[1] <= 'z' ? ext[0] | ext[1] << 8 | 2105376 : ext.Length == 3 && ext[0] <= 'z' && ext[1] <= 'z' && ext[2] <= 'z' ? ext[0] | ext[1] << 8 | ext[2] << 16 | 2105376 : 0;
		}

		internal static int GetPackedExt(string filename)
		{
			int ext = 0;
			for (int i = filename.Length; --i > 0;) {
				int c = filename[i];
				if (c <= 32 || c > 122)
					return 0;
				if (c == 46)
					return ext | 2105376;
				ext = (ext << 8) + c;
			}
			return 0;
		}

		static bool IsOurPackedExt(int ext)
		{
			switch (ext) {
			case 7364979:
			case 6516067:
			case 3370339:
			case 7499107:
			case 7564643:
			case 6516068:
			case 7629924:
			case 7630957:
			case 6582381:
			case 7630194:
			case 6516084:
			case 3698036:
			case 3304820:
			case 2122598:
				return true;
			default:
				return false;
			}
		}

		/**
		/// Checks whether the filename represents a module type supported by ASAP.
		/// Returns <see langword="true" /> if the filename is supported by ASAP.
		 * @param filename Filename to check the extension of.
		 */
		public static bool IsOurFile(string filename)
		{
			return IsOurPackedExt(GetPackedExt(filename));
		}

		/**
		/// Checks whether the filename extension represents a module type supported by ASAP.
		/// Returns <see langword="true" /> if the filename extension is supported by ASAP.
		 * @param ext Filename extension without the leading dot.
		 */
		public static bool IsOurExt(string ext)
		{
			return IsOurPackedExt(PackExt(ext));
		}

		static int GuessPackedExt(byte[] module, int moduleLen)
		{
			if (ValidateSap(module, moduleLen))
				return 7364979;
			if (ValidateFc(module, moduleLen))
				return 2122598;
			if (ValidateRmt(module, moduleLen))
				return 7630194;
			throw new Exception("Unknown format");
		}

		/**
		/// Loads file information.
		 * @param filename Filename, used to determine the format.
		 * @param module Contents of the file.
		 * @param moduleLen Length of the file.
		 */
		public void Load(string filename, byte[] module, int moduleLen)
		{
			int ext;
			if (filename != null) {
				int len = filename.Length;
				int basename = 0;
				ext = -1;
				for (int i = len; --i >= 0;) {
					int c = filename[i];
					if (c == 47 || c == 92) {
						basename = i + 1;
						break;
					}
					if (c == 46)
						ext = i;
				}
				if (ext < 0)
					throw new Exception("Filename has no extension");
				ext -= basename;
				if (ext > 127)
					ext = 127;
				this.Filename = filename.Substring(basename, ext);
				ext = GetPackedExt(filename);
			}
			else {
				this.Filename = "";
				ext = GuessPackedExt(module, moduleLen);
			}
			this.Author = "";
			this.Title = "";
			this.Date = "";
			this.Channels = 1;
			this.Songs = 1;
			this.DefaultSong = 0;
			for (int i = 0; i < 32; i++) {
				this.Durations[i] = -1;
				this.Loops[i] = false;
			}
			this.Ntsc = false;
			this.Fastplay = 312;
			this.Music = -1;
			this.Init = -1;
			this.Player = -1;
			this.CovoxAddr = -1;
			this.HeaderLen = 0;
			switch (ext) {
			case 7364979:
				ParseSap(module, moduleLen);
				return;
			case 6516067:
				ParseCmc(module, moduleLen, ASAPModuleType.Cmc);
				return;
			case 3370339:
				ParseCmc(module, moduleLen, ASAPModuleType.Cm3);
				return;
			case 7499107:
				ParseCmc(module, moduleLen, ASAPModuleType.Cmr);
				return;
			case 7564643:
				this.Channels = 2;
				ParseCmc(module, moduleLen, ASAPModuleType.Cms);
				return;
			case 6516068:
				this.Fastplay = 156;
				ParseCmc(module, moduleLen, ASAPModuleType.Cmc);
				return;
			case 7629924:
				ParseDlt(module, moduleLen);
				return;
			case 7630957:
				ParseMpt(module, moduleLen);
				return;
			case 6582381:
				this.Fastplay = 156;
				ParseMpt(module, moduleLen);
				return;
			case 7630194:
				ParseRmt(module, moduleLen);
				return;
			case 6516084:
			case 3698036:
				ParseTmc(module, moduleLen);
				return;
			case 3304820:
				ParseTm2(module, moduleLen);
				return;
			case 2122598:
				ParseFc(module, moduleLen);
				return;
			default:
				throw new Exception("Unknown filename extension");
			}
		}

		/**
		/// Returns human-readable description of the filename extension.
		 * @param ext Filename extension without the leading dot.
		 */
		public static string GetExtDescription(string ext)
		{
			switch (PackExt(ext)) {
			case 7364979:
				return "Slight Atari Player";
			case 6516067:
				return "Chaos Music Composer";
			case 3370339:
				return "CMC \"3/4\"";
			case 7499107:
				return "CMC \"Rzog\"";
			case 7564643:
				return "Stereo Double CMC";
			case 6516068:
				return "DoublePlay CMC";
			case 7629924:
				return "Delta Music Composer";
			case 7630957:
				return "Music ProTracker";
			case 6582381:
				return "MPT DoublePlay";
			case 7630194:
				return "Raster Music Tracker";
			case 6516084:
			case 3698036:
				return "Theta Music Composer 1.x";
			case 3304820:
				return "Theta Music Composer 2.x";
			case 2122598:
				return "Future Composer";
			case 7890296:
				return "Atari 8-bit executable";
			default:
				throw new Exception("Unknown extension");
			}
		}

		internal int GetRmtSapOffset(byte[] module, int moduleLen)
		{
			if (this.Player != 13315)
				return -1;
			int offset = this.HeaderLen + GetWord(module, this.HeaderLen + 4) - GetWord(module, this.HeaderLen + 2) + 7;
			if (offset + 6 >= moduleLen || module[offset + 4] != 82 || module[offset + 5] != 77 || module[offset + 6] != 84)
				return -1;
			return offset;
		}

		internal ASAPModuleType GetOriginalModuleType(byte[] module, int moduleLen)
		{
			switch (this.Type) {
			case ASAPModuleType.SapB:
				if ((this.Init == 1019 || this.Init == 1017) && this.Player == 1283)
					return ASAPModuleType.Dlt;
				if (((this.Init == 1267 || this.Init == 1263) && this.Player == 1283) || (this.Init == 62707 && this.Player == 62723))
					return ASAPModuleType.Mpt;
				if (this.Init == 3200 || GetRmtSapOffset(module, moduleLen) > 0)
					return ASAPModuleType.Rmt;
				if (this.Init == 1269 || this.Init == 62709 || this.Init == 1266 || ((this.Init == 1255 || this.Init == 62695 || this.Init == 1252) && this.Fastplay == 156) || ((this.Init == 1253 || this.Init == 62693 || this.Init == 1250) && (this.Fastplay == 104 || this.Fastplay == 78)))
					return ASAPModuleType.Tmc;
				if ((this.Init == 4224 && this.Player == 1283) || (this.Init == 4992 && this.Player == 2051))
					return ASAPModuleType.Tm2;
				if (this.Init == 1024 && this.Player == 1027)
					return ASAPModuleType.Fc;
				return this.Type;
			case ASAPModuleType.SapC:
				if ((this.Player == 1280 || this.Player == 62720) && moduleLen >= 1024) {
					if (this.Channels > 1)
						return ASAPModuleType.Cms;
					if (module[moduleLen - 170] == 30)
						return ASAPModuleType.Cmr;
					if (module[moduleLen - 909] == 48)
						return ASAPModuleType.Cm3;
					return ASAPModuleType.Cmc;
				}
				return this.Type;
			default:
				return this.Type;
			}
		}

		/**
		/// Returns the extension of the original module format.
		/// For native modules it simply returns their extension.
		/// For the SAP format it attempts to detect the original module format.
		 * @param module Contents of the file.
		 * @param moduleLen Length of the file.
		 */
		public string GetOriginalModuleExt(byte[] module, int moduleLen)
		{
			switch (GetOriginalModuleType(module, moduleLen)) {
			case ASAPModuleType.Cmc:
				return this.Fastplay == 156 ? "dmc" : "cmc";
			case ASAPModuleType.Cm3:
				return "cm3";
			case ASAPModuleType.Cmr:
				return "cmr";
			case ASAPModuleType.Cms:
				return "cms";
			case ASAPModuleType.Dlt:
				return "dlt";
			case ASAPModuleType.Mpt:
				return this.Fastplay == 156 ? "mpd" : "mpt";
			case ASAPModuleType.Rmt:
				return "rmt";
			case ASAPModuleType.Tmc:
				return "tmc";
			case ASAPModuleType.Tm2:
				return "tm2";
			case ASAPModuleType.Fc:
				return "fc";
			default:
				return null;
			}
		}

		static readonly byte[] GetRmtInstrumentFramesrmtVolumeSilent = { 16, 8, 4, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1 };
	}

	public class Cpu6502
	{

		const int VFlag = 64;

		const int DFlag = 8;

		const int IFlag = 4;

		const int ZFlag = 2;

		internal ASAP Asap;

		internal readonly byte[] Memory = new byte[65536];

		internal int Cycle;

		internal int Pc;

		internal int A;

		internal int X;

		internal int Y;

		internal int S;

		int Nz;

		int C;

		int Vdi;

		int instructionNumber = 0; //added

		internal bool OsRomInit = false;

		//matosimi:
		public void SetInterruptFlag(bool value)
		{
			if (value) Vdi |= 4; else Vdi &= 0xff - 4;
		}

		internal void Reset()
		{
			this.Cycle = 0;
			this.Nz = 0;
			this.C = 0;
			this.Vdi = 0;
		}

		public int Peek(int addr)
		{
			if ((addr & 63744) == 53248)
				return this.Asap.PeekHardware(addr);
			else
				return this.Memory[addr];
		}

		void Poke(int addr, int data)
		{
			if (addr > 0xC000)
				this.Asap.PokeHardware(addr, data);
			else
				this.Memory[addr] = (byte)data;

			/*
            if ((addr & 0xF900) == 0xD000)
                this.atari.PokeHardware(addr, data);
            else
                this.Memory[addr] = (byte)data;
            */
		}

		int PeekReadModifyWrite(int addr)
		{
			if (addr >> 8 == 210) {
				this.Cycle--;
				int data = this.Asap.PeekHardware(addr);
				this.Asap.PokeHardware(addr, data);
				this.Cycle++;
				return data;
			}
			return this.Memory[addr];
		}

		int Pull()
		{
			int s = (this.S + 1) & 255;
			this.S = s;
			return this.Memory[256 + s];
		}

		void PullFlags()
		{
			int data = Pull();
			this.Nz = ((data & 128) << 1) + (~data & 2);
			this.C = data & 1;
			this.Vdi = data & 76;
		}

		void Push(int data)
		{
			int s = this.S;
			this.Memory[256 + s] = (byte) data;
			this.S = (s - 1) & 255;
		}

		internal void PushPc()
		{
			Push(this.Pc >> 8);
			Push(this.Pc & 255);
		}

		internal void PushFlags(int b)
		{
			int nz = this.Nz;
			b += ((nz | nz >> 1) & 128) + this.Vdi + this.C;
			if ((nz & 255) == 0)
				b += 2;
			Push(b);
		}

		void AddWithCarry(int data)
		{
			int a = this.A;
			int vdi = this.Vdi;
			int tmp = a + data + this.C;
			this.Nz = tmp & 255;
			if ((vdi & 8) == 0) {
				this.Vdi = (vdi & 12) + ((~(data ^ a) & (a ^ tmp)) >> 1 & 64);
				this.C = tmp >> 8;
				this.A = this.Nz;
			}
			else {
				int al = (a & 15) + (data & 15) + this.C;
				if (al >= 10) {
					tmp += al < 26 ? 6 : -10;
					if (this.Nz != 0)
						this.Nz = (tmp & 128) + 1;
				}
				this.Vdi = (vdi & 12) + ((~(data ^ a) & (a ^ tmp)) >> 1 & 64);
				if (tmp >= 160) {
					this.C = 1;
					this.A = (tmp - 160) & 255;
				}
				else {
					this.C = 0;
					this.A = tmp;
				}
			}
		}

		void SubtractWithCarry(int data)
		{
			int a = this.A;
			int vdi = this.Vdi;
			int borrow = this.C - 1;
			int tmp = a - data + borrow;
			int al = (a & 15) - (data & 15) + borrow;
			this.Vdi = (vdi & 12) + (((data ^ a) & (a ^ tmp)) >> 1 & 64);
			this.C = tmp >= 0 ? 1 : 0;
			this.Nz = this.A = tmp & 255;
			if ((vdi & 8) != 0) {
				if (al < 0)
					this.A += al < -10 ? 10 : -6;
				if (this.C == 0)
					this.A = (this.A - 96) & 255;
			}
		}

		int ArithmeticShiftLeft(int addr)
		{
			int data = PeekReadModifyWrite(addr);
			this.C = data >> 7;
			data = data << 1 & 255;
			Poke(addr, data);
			return data;
		}

		int RotateLeft(int addr)
		{
			int data = (PeekReadModifyWrite(addr) << 1) + this.C;
			this.C = data >> 8;
			data &= 255;
			Poke(addr, data);
			return data;
		}

		int LogicalShiftRight(int addr)
		{
			int data = PeekReadModifyWrite(addr);
			this.C = data & 1;
			data >>= 1;
			Poke(addr, data);
			return data;
		}

		int RotateRight(int addr)
		{
			int data = (this.C << 8) + PeekReadModifyWrite(addr);
			this.C = data & 1;
			data >>= 1;
			Poke(addr, data);
			return data;
		}

		int Decrement(int addr)
		{
			int data = (PeekReadModifyWrite(addr) - 1) & 255;
			Poke(addr, data);
			return data;
		}

		int Increment(int addr)
		{
			int data = (PeekReadModifyWrite(addr) + 1) & 255;
			Poke(addr, data);
			return data;
		}

		void ExecuteIrq(int b)
		{
			PushPc();
			PushFlags(b);
			this.Vdi |= 4;
			this.Pc = this.Memory[65534] + (this.Memory[65535] << 8);
		}

		void CheckIrq()
		{
			if ((this.Vdi & 4) == 0 && this.Asap.IsIrq()) {
				this.Cycle += 7;
				ExecuteIrq(32);
			}
		}

		void Shx(int addr, int data)
		{
			addr += this.Memory[this.Pc++];
			int hi = this.Memory[this.Pc++];
			data &= hi + 1;
			if (addr >= 256)
				hi = data - 1;
			addr += hi << 8;
			Poke(addr, data);
		}

		System.IO.StreamWriter logfile;
		bool enableLogging = false;
		internal void Log(string text)
        {
			if (!enableLogging)
				return;
			Console.WriteLine(text);
			if (logfile == null)
				logfile = new System.IO.StreamWriter("cpulog.txt");
			logfile.WriteLine(text);
        }

		/**
		/// Runs 6502 emulation for the specified number of Atari scanlines.
		/// Each scanline is 114 cycles of which 9 is taken by ANTIC for memory refresh.
		 */
		internal void DoFrame(int cycleLimit)
		{
			while (this.Cycle < cycleLimit) {
				if (this.Cycle >= this.Asap.NextEventCycle) {
					this.Asap.HandleEvent();
					CheckIrq();
				}
				if (this.Pc == this.Asap.PlayerUpdateAddr)
					this.Asap.OnPlayerUpdate();   // SAP-R export: CMS replay update entry
				int data = this.Memory[this.Pc++];
				this.Cycle += DoFrameopcodeCycles[data];
				int addr = 0;
				//added
				Log(Pc.ToString("x") + " " + Pc.ToString() + " A:" + A.ToString());
				
				//atarixl.rom
				if (OsRomInit && Pc == 0x5003 + 1)
				{
					enableLogging = true;
					Log("OS ROM initialized");
					Console.WriteLine("Os ROM initialized");
					data = 210; //KIL
					Memory[0x5003] = (byte)data; //KIL
					Pc = 0x5003;
					OsRomInit = false;
				}
				// ---

				switch (data) {
				case 0:
					this.Pc++;
					ExecuteIrq(48);
					continue;
				case 1:
				case 3:
				case 33:
				case 35:
				case 65:
				case 67:
				case 97:
				case 99:
				case 129:
				case 131:
				case 161:
				case 163:
				case 193:
				case 195:
				case 225:
				case 227:
					addr = (this.Memory[this.Pc++] + this.X) & 255;
					addr = this.Memory[addr] + (this.Memory[(addr + 1) & 255] << 8);
					break;
				case 2:
				case 18:
				case 34:
				case 50:
				case 66:
				case 82:
				case 98:
				case 114:
				case 146:
				case 178:
				case 210:
				case 242:
					this.Pc--;
					this.Cycle = this.Asap.NextEventCycle;
					continue;
				case 4:
				case 68:
				case 100:
				case 20:
				case 52:
				case 84:
				case 116:
				case 212:
				case 244:
				case 128:
				case 130:
				case 137:
				case 194:
				case 226:
					this.Pc++;
					continue;
				case 5:
				case 6:
				case 7:
				case 36:
				case 37:
				case 38:
				case 39:
				case 69:
				case 70:
				case 71:
				case 101:
				case 102:
				case 103:
				case 132:
				case 133:
				case 134:
				case 135:
				case 164:
				case 165:
				case 166:
				case 167:
				case 196:
				case 197:
				case 198:
				case 199:
				case 228:
				case 229:
				case 230:
				case 231:
					addr = this.Memory[this.Pc++];
					break;
				case 8:
					PushFlags(48);
					continue;
				case 9:
				case 41:
				case 73:
				case 105:
				case 160:
				case 162:
				case 169:
				case 192:
				case 201:
				case 224:
				case 233:
				case 235:
					addr = this.Pc++;
					break;
				case 10:
					this.C = this.A >> 7;
					this.Nz = this.A = this.A << 1 & 255;
					continue;
				case 11:
				case 43:
					this.Nz = this.A &= this.Memory[this.Pc++];
					this.C = this.Nz >> 7;
					continue;
				case 12:
					this.Pc += 2;
					continue;
				case 13:
				case 14:
				case 15:
				case 44:
				case 45:
				case 46:
				case 47:
				case 77:
				case 78:
				case 79:
				case 108:
				case 109:
				case 110:
				case 111:
				case 140:
				case 141:
				case 142:
				case 143:
				case 172:
				case 173:
				case 174:
				case 175:
				case 204:
				case 205:
				case 206:
				case 207:
				case 236:
				case 237:
				case 238:
				case 239:
					addr = this.Memory[this.Pc++];
					addr += this.Memory[this.Pc++] << 8;
					break;
				case 16:
					if (this.Nz < 128)
						break;
					this.Pc++;
					continue;
				case 17:
				case 49:
				case 81:
				case 113:
				case 177:
				case 179:
				case 209:
				case 241:
					int zp = this.Memory[this.Pc++];
					addr = this.Memory[zp] + this.Y;
					if (addr >= 256)
						this.Cycle++;
					addr = (addr + (this.Memory[(zp + 1) & 255] << 8)) & 65535;
					break;
				case 19:
				case 51:
				case 83:
				case 115:
				case 145:
				case 211:
				case 243:
					addr = this.Memory[this.Pc++];
					addr = (this.Memory[addr] + (this.Memory[(addr + 1) & 255] << 8) + this.Y) & 65535;
					break;
				case 21:
				case 22:
				case 23:
				case 53:
				case 54:
				case 55:
				case 85:
				case 86:
				case 87:
				case 117:
				case 118:
				case 119:
				case 148:
				case 149:
				case 180:
				case 181:
				case 213:
				case 214:
				case 215:
				case 245:
				case 246:
				case 247:
					addr = (this.Memory[this.Pc++] + this.X) & 255;
					break;
				case 24:
					this.C = 0;
					continue;
				case 25:
				case 57:
				case 89:
				case 121:
				case 185:
				case 187:
				case 190:
				case 191:
				case 217:
				case 249:
					addr = this.Memory[this.Pc++] + this.Y;
					if (addr >= 256)
						this.Cycle++;
					addr = (addr + (this.Memory[this.Pc++] << 8)) & 65535;
					break;
				case 27:
				case 59:
				case 91:
				case 123:
				case 153:
				case 219:
				case 251:
					addr = this.Memory[this.Pc++] + this.Y;
					addr = (addr + (this.Memory[this.Pc++] << 8)) & 65535;
					break;
				case 28:
				case 60:
				case 92:
				case 124:
				case 220:
				case 252:
					if (this.Memory[this.Pc] + this.X >= 256)
						this.Cycle++;
					this.Pc += 2;
					continue;
				case 29:
				case 61:
				case 93:
				case 125:
				case 188:
				case 189:
				case 221:
				case 253:
					addr = this.Memory[this.Pc++] + this.X;
					if (addr >= 256)
						this.Cycle++;
					addr = (addr + (this.Memory[this.Pc++] << 8)) & 65535;
					break;
				case 30:
				case 31:
				case 62:
				case 63:
				case 94:
				case 95:
				case 126:
				case 127:
				case 157:
				case 222:
				case 223:
				case 254:
				case 255:
					addr = this.Memory[this.Pc++] + this.X;
					addr = (addr + (this.Memory[this.Pc++] << 8)) & 65535;
					break;
				case 32:
					addr = this.Memory[this.Pc++];
					PushPc();
					this.Pc = addr + (this.Memory[this.Pc] << 8);
					continue;
				case 40:
					PullFlags();
					CheckIrq();
					continue;
				case 42:
					this.A = (this.A << 1) + this.C;
					this.C = this.A >> 8;
					this.Nz = this.A &= 255;
					continue;
				case 48:
					if (this.Nz >= 128)
						break;
					this.Pc++;
					continue;
				case 56:
					this.C = 1;
					continue;
				case 64:
					PullFlags();
					this.Pc = Pull();
					this.Pc += Pull() << 8;
					CheckIrq();
					continue;
				case 72:
					Push(this.A);
					continue;
				case 74:
					this.C = this.A & 1;
					this.Nz = this.A >>= 1;
					continue;
				case 75:
					this.A &= this.Memory[this.Pc++];
					this.C = this.A & 1;
					this.Nz = this.A >>= 1;
					continue;
				case 76:
					addr = this.Memory[this.Pc++];
					this.Pc = addr + (this.Memory[this.Pc] << 8);
					continue;
				case 80:
					if ((this.Vdi & 64) == 0)
						break;
					this.Pc++;
					continue;
				case 88:
					this.Vdi &= 72;
					CheckIrq();
					continue;
				case 96:
					this.Pc = Pull();
					this.Pc += (Pull() << 8) + 1;
					continue;
				case 104:
					this.Nz = this.A = Pull();
					continue;
				case 106:
					this.Nz = (this.C << 7) + (this.A >> 1);
					this.C = this.A & 1;
					this.A = this.Nz;
					continue;
				case 107:
					data = this.A & this.Memory[this.Pc++];
					this.Nz = this.A = (data >> 1) + (this.C << 7);
					this.Vdi = (this.Vdi & 12) + ((this.A ^ data) & 64);
					if ((this.Vdi & 8) == 0)
						this.C = data >> 7;
					else {
						if ((data & 15) >= 5)
							this.A = (this.A & 240) + ((this.A + 6) & 15);
						if (data >= 80) {
							this.A = (this.A + 96) & 255;
							this.C = 1;
						}
						else
							this.C = 0;
					}
					continue;
				case 112:
					if ((this.Vdi & 64) != 0)
						break;
					this.Pc++;
					continue;
				case 120:
					this.Vdi |= 4;
					continue;
				case 136:
					this.Nz = this.Y = (this.Y - 1) & 255;
					continue;
				case 138:
					this.Nz = this.A = this.X;
					continue;
				case 139:
					data = this.Memory[this.Pc++];
					this.A &= (data | 239) & this.X;
					this.Nz = this.A & data;
					continue;
				case 144:
					if (this.C == 0)
						break;
					this.Pc++;
					continue;
				case 147:
					{
						addr = this.Memory[this.Pc++];
						int hi = this.Memory[(addr + 1) & 255];
						addr = this.Memory[addr];
						data = (hi + 1) & this.A & this.X;
						addr += this.Y;
						if (addr >= 256)
							hi = data - 1;
						addr += hi << 8;
						Poke(addr, data);
					}
					continue;
				case 150:
				case 151:
				case 182:
				case 183:
					addr = (this.Memory[this.Pc++] + this.Y) & 255;
					break;
				case 152:
					this.Nz = this.A = this.Y;
					continue;
				case 154:
					this.S = this.X;
					continue;
				case 155:
					this.S = this.A & this.X;
					Shx(this.Y, this.S);
					continue;
				case 156:
					Shx(this.X, this.Y);
					continue;
				case 158:
					Shx(this.Y, this.X);
					continue;
				case 159:
					Shx(this.Y, this.A & this.X);
					continue;
				case 168:
					this.Nz = this.Y = this.A;
					continue;
				case 170:
					this.Nz = this.X = this.A;
					continue;
				case 171:
					this.Nz = this.X = this.A &= this.Memory[this.Pc++];
					continue;
				case 176:
					if (this.C != 0)
						break;
					this.Pc++;
					continue;
				case 184:
					this.Vdi &= 12;
					continue;
				case 186:
					this.Nz = this.X = this.S;
					continue;
				case 200:
					this.Nz = this.Y = (this.Y + 1) & 255;
					continue;
				case 202:
					this.Nz = this.X = (this.X - 1) & 255;
					continue;
				case 203:
					this.Nz = this.Memory[this.Pc++];
					this.X &= this.A;
					this.C = this.X >= this.Nz ? 1 : 0;
					this.Nz = this.X = (this.X - this.Nz) & 255;
					continue;
				case 208:
					if ((this.Nz & 255) != 0)
						break;
					this.Pc++;
					continue;
				case 216:
					this.Vdi &= 68;
					continue;
				case 232:
					this.Nz = this.X = (this.X + 1) & 255;
					continue;
				case 234:
				case 26:
				case 58:
				case 90:
				case 122:
				case 218:
				case 250:
					continue;
				case 240:
					if ((this.Nz & 255) == 0)
						break;
					this.Pc++;
					continue;
				case 248:
					this.Vdi |= 8;
					continue;
				default:
					throw new NotImplementedException();
				}
				switch (data) {
				case 1:
				case 5:
				case 9:
				case 13:
				case 17:
				case 21:
				case 25:
				case 29:
					this.Nz = this.A |= Peek(addr);
					break;
				case 3:
				case 7:
				case 15:
				case 19:
				case 23:
				case 27:
				case 31:
					this.Nz = this.A |= ArithmeticShiftLeft(addr);
					break;
				case 6:
				case 14:
				case 22:
				case 30:
					this.Nz = ArithmeticShiftLeft(addr);
					break;
				case 16:
				case 48:
				case 80:
				case 112:
				case 144:
				case 176:
				case 208:
				case 240:
					addr = (this.Memory[this.Pc] ^ 128) - 128;
					this.Pc++;
					addr += this.Pc;
					this.Cycle += (addr ^ this.Pc) >> 8 != 0 ? 2 : 1;
					this.Pc = addr;
					break;
				case 33:
				case 37:
				case 41:
				case 45:
				case 49:
				case 53:
				case 57:
				case 61:
					this.Nz = this.A &= Peek(addr);
					break;
				case 35:
				case 39:
				case 47:
				case 51:
				case 55:
				case 59:
				case 63:
					this.Nz = this.A &= RotateLeft(addr);
					break;
				case 36:
				case 44:
					this.Nz = Peek(addr);
					this.Vdi = (this.Vdi & 12) + (this.Nz & 64);
					this.Nz = ((this.Nz & 128) << 1) + (this.Nz & this.A);
					break;
				case 38:
				case 46:
				case 54:
				case 62:
					this.Nz = RotateLeft(addr);
					break;
				case 65:
				case 69:
				case 73:
				case 77:
				case 81:
				case 85:
				case 89:
				case 93:
					this.Nz = this.A ^= Peek(addr);
					break;
				case 67:
				case 71:
				case 79:
				case 83:
				case 87:
				case 91:
				case 95:
					this.Nz = this.A ^= LogicalShiftRight(addr);
					break;
				case 70:
				case 78:
				case 86:
				case 94:
					this.Nz = LogicalShiftRight(addr);
					break;
				case 97:
				case 101:
				case 105:
				case 109:
				case 113:
				case 117:
				case 121:
				case 125:
					AddWithCarry(Peek(addr));
					break;
				case 99:
				case 103:
				case 111:
				case 115:
				case 119:
				case 123:
				case 127:
					AddWithCarry(RotateRight(addr));
					break;
				case 102:
				case 110:
				case 118:
				case 126:
					this.Nz = RotateRight(addr);
					break;
				case 108:
					this.Pc = this.Memory[addr];
					if ((++addr & 255) == 0)
						addr -= 255;
					this.Pc += this.Memory[addr] << 8;
					break;
				case 129:
				case 133:
				case 141:
				case 145:
				case 149:
				case 153:
				case 157:
					Poke(addr, this.A);
					break;
				case 131:
				case 135:
				case 143:
				case 151:
					Poke(addr, this.A & this.X);
					break;
				case 132:
				case 140:
				case 148:
					Poke(addr, this.Y);
					break;
				case 134:
				case 142:
				case 150:
					Poke(addr, this.X);
					break;
				case 160:
				case 164:
				case 172:
				case 180:
				case 188:
					this.Nz = this.Y = Peek(addr);
					break;
				case 161:
				case 165:
				case 169:
				case 173:
				case 177:
				case 181:
				case 185:
				case 189:
					this.Nz = this.A = Peek(addr);
					break;
				case 162:
				case 166:
				case 174:
				case 182:
				case 190:
					this.Nz = this.X = Peek(addr);
					break;
				case 163:
				case 167:
				case 175:
				case 179:
				case 183:
				case 191:
					this.Nz = this.X = this.A = Peek(addr);
					break;
				case 187:
					this.Nz = this.X = this.A = this.S &= Peek(addr);
					break;
				case 192:
				case 196:
				case 204:
					this.Nz = Peek(addr);
					this.C = this.Y >= this.Nz ? 1 : 0;
					this.Nz = (this.Y - this.Nz) & 255;
					break;
				case 193:
				case 197:
				case 201:
				case 205:
				case 209:
				case 213:
				case 217:
				case 221:
					this.Nz = Peek(addr);
					this.C = this.A >= this.Nz ? 1 : 0;
					this.Nz = (this.A - this.Nz) & 255;
					break;
				case 195:
				case 199:
				case 207:
				case 211:
				case 215:
				case 219:
				case 223:
					data = Decrement(addr);
					this.C = this.A >= data ? 1 : 0;
					this.Nz = (this.A - data) & 255;
					break;
				case 198:
				case 206:
				case 214:
				case 222:
					this.Nz = Decrement(addr);
					break;
				case 224:
				case 228:
				case 236:
					this.Nz = Peek(addr);
					this.C = this.X >= this.Nz ? 1 : 0;
					this.Nz = (this.X - this.Nz) & 255;
					break;
				case 225:
				case 229:
				case 233:
				case 235:
				case 237:
				case 241:
				case 245:
				case 249:
				case 253:
					SubtractWithCarry(Peek(addr));
					break;
				case 227:
				case 231:
				case 239:
				case 243:
				case 247:
				case 251:
				case 255:
					SubtractWithCarry(Increment(addr));
					break;
				case 230:
				case 238:
				case 246:
				case 254:
					this.Nz = Increment(addr);
					break;
				default:
					throw new NotImplementedException();
				}
			}
		}

		static readonly byte[] DoFrameopcodeCycles = { 7, 6, 2, 8, 3, 3, 5, 5, 3, 2, 2, 2, 4, 4, 6, 6,
			2, 5, 2, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
			6, 6, 2, 8, 3, 3, 5, 5, 4, 2, 2, 2, 4, 4, 6, 6,
			2, 5, 2, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
			6, 6, 2, 8, 3, 3, 5, 5, 3, 2, 2, 2, 3, 4, 6, 6,
			2, 5, 2, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
			6, 6, 2, 8, 3, 3, 5, 5, 4, 2, 2, 2, 5, 4, 6, 6,
			2, 5, 2, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
			2, 6, 2, 6, 3, 3, 3, 3, 2, 2, 2, 2, 4, 4, 4, 4,
			2, 6, 2, 6, 4, 4, 4, 4, 2, 5, 2, 5, 5, 5, 5, 5,
			2, 6, 2, 6, 3, 3, 3, 3, 2, 2, 2, 2, 4, 4, 4, 4,
			2, 5, 2, 5, 4, 4, 4, 4, 2, 4, 2, 4, 4, 4, 4, 4,
			2, 6, 2, 8, 3, 3, 5, 5, 2, 2, 2, 2, 4, 4, 6, 6,
			2, 5, 2, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7,
			2, 6, 2, 8, 3, 3, 5, 5, 2, 2, 2, 2, 4, 4, 6, 6,
			2, 5, 2, 8, 4, 4, 6, 6, 2, 4, 2, 7, 4, 4, 7, 7 };
	}

	class PokeyChannel
	{

		const int MuteFrequency = 1;

		const int MuteInit = 2;

		const int MuteUser = 4;

		const int MuteSerialInput = 8;

		internal int Audf;

		internal int Audc;

		internal int PeriodCycles;

		internal int TickCycle;

		internal int TimerCycle;

		internal int Mute;

		int Out;

		internal int Delta;

		internal void Initialize()
		{
			this.Audf = 0;
			this.Audc = 0;
			this.PeriodCycles = 28;
			this.TickCycle = 8388608;
			this.TimerCycle = 8388608;
			this.Mute = 1;
			this.Out = 0;
			this.Delta = 0;
		}

		internal void Slope(Pokey pokey, PokeyPair pokeys, int cycle)
		{
			this.Delta = -this.Delta;
			pokey.AddDelta(pokeys, cycle, this.Delta);
		}

		internal void DoTick(Pokey pokey, PokeyPair pokeys, int cycle, int ch)
		{
			this.TickCycle += this.PeriodCycles;
			int audc = this.Audc;
			if ((audc & 176) == 160)
				this.Out ^= 1;
			else if ((audc & 16) != 0 || pokey.Init)
				return;
			else {
				int poly = cycle + pokey.PolyIndex - ch;
				if (audc < 128 && (1706902752 & 1 << poly % 31) == 0)
					return;
				if ((audc & 32) != 0)
					this.Out ^= 1;
				else {
					int newOut;
					if ((audc & 64) != 0)
						newOut = 21360 >> poly % 15;
					else if (pokey.Audctl < 128) {
						poly %= 131071;
						newOut = pokeys.Poly17Lookup[poly >> 3] >> (poly & 7);
					}
					else
						newOut = pokeys.Poly9Lookup[poly % 511];
					newOut &= 1;
					if (this.Out == newOut)
						return;
					this.Out = newOut;
				}
			}
			Slope(pokey, pokeys, cycle);
		}

		internal void DoStimer(int cycle)
		{
			if (this.TickCycle != 8388608)
				this.TickCycle = cycle + this.PeriodCycles;
		}

		internal void SetMute(bool enable, int mask, int cycle)
		{
			if (enable) {
				this.Mute |= mask;
				this.TickCycle = 8388608;
			}
			else {
				this.Mute &= ~mask;
				if (this.Mute == 0 && this.TickCycle == 8388608)
					this.TickCycle = cycle;
			}
		}

		internal void MuteUltrasound(int cycle)
		{
			SetMute(this.PeriodCycles <= 112 && (this.Audc & 176) == 160, 1, cycle);
		}

		internal void SetAudc(Pokey pokey, PokeyPair pokeys, int data, int cycle)
		{
			if (this.Audc == data)
				return;
			pokey.GenerateUntilCycle(pokeys, cycle);
			this.Audc = data;
			if ((data & 16) != 0) {
				data = (data & 15) << 20;
				if ((this.Mute & 4) == 0)
					pokey.AddDelta(pokeys, cycle, this.Delta > 0 ? data - this.Delta : data);
				this.Delta = data;
			}
			else {
				data = (data & 15) << 20;
				MuteUltrasound(cycle);
				if (this.Delta > 0) {
					if ((this.Mute & 4) == 0)
						pokey.AddDelta(pokeys, cycle, data - this.Delta);
					this.Delta = data;
				}
				else
					this.Delta = -data;
			}
		}

		internal void EndFrame(int cycle)
		{
			if (this.TimerCycle != 8388608)
				this.TimerCycle -= cycle;
		}
	}

	class Pokey
	{
		internal Pokey()
		{
			for (int _i0 = 0; _i0 < 4; _i0++) {
				this.Channels[_i0] = new PokeyChannel();
			}
		}

		const int NeverCycle = 8388608;

		const int SampleRate = 44100;

		internal readonly PokeyChannel[] Channels = new PokeyChannel[4];

		internal int Audctl;

		int Skctl;

		internal int Irqst;

		internal bool Init;

		int DivCycles;

		int ReloadCycles1;

		int ReloadCycles3;

		internal int PolyIndex;

		readonly int[] DeltaBuffer = new int[888];

		int IirAcc;

		internal void StartFrame()
		{
			Array.Clear(this.DeltaBuffer, 0, 888);
		}

		internal void Initialize()
		{
			for (int i = 0; i < 4; i++)
				this.Channels[i].Initialize();
			this.Audctl = 0;
			this.Skctl = 3;
			this.Irqst = 255;
			this.Init = false;
			this.DivCycles = 28;
			this.ReloadCycles1 = 28;
			this.ReloadCycles3 = 28;
			this.PolyIndex = 60948015;
			this.IirAcc = 0;
			StartFrame();
		}

		internal void AddDelta(PokeyPair pokeys, int cycle, int delta)
		{
			int i = cycle * pokeys.SampleFactor + pokeys.SampleOffset;
			int delta2 = (delta >> 16) * (i >> 4 & 65535);
			i >>= 20;
			this.DeltaBuffer[i] += delta - delta2;
			this.DeltaBuffer[i + 1] += delta2;
		}

		/**
		/// Fills <c>DeltaBuffer</c> up to <c>cycleLimit</c> basing on current Audf/Audc/Audctl values.
		 */
		internal void GenerateUntilCycle(PokeyPair pokeys, int cycleLimit)
		{
			int same = 0;
			int cycleStore = 0;
			for (;;) {
				int cycle = cycleLimit;
				for (int i = 0; i < 4; i++) {
					int tickCycle = this.Channels[i].TickCycle;
					if (cycle > tickCycle)
						cycle = tickCycle;
				}
				//shit
				/*if (cycle != cycleStore)
				{
					same = 0;
					cycleStore = cycle;
				}
				else
					same++;*/
				//-shit
				if (cycle == cycleLimit ) /*shit+*/ //|| same > 100)
					break;
				if (cycle == this.Channels[2].TickCycle) {
					if ((this.Audctl & 4) != 0 && this.Channels[0].Delta > 0 && this.Channels[0].Mute == 0)
						this.Channels[0].Slope(this, pokeys, cycle);
					this.Channels[2].DoTick(this, pokeys, cycle, 2);
				}
				if (cycle == this.Channels[3].TickCycle) {
					if ((this.Audctl & 8) != 0)
						this.Channels[2].TickCycle = cycle + this.ReloadCycles3;
					if ((this.Audctl & 2) != 0 && this.Channels[1].Delta > 0 && this.Channels[1].Mute == 0)
						this.Channels[1].Slope(this, pokeys, cycle);
					this.Channels[3].DoTick(this, pokeys, cycle, 3);
				}
				if (cycle == this.Channels[0].TickCycle) {
					if ((this.Skctl & 136) == 8)
						this.Channels[1].TickCycle = cycle + this.Channels[1].PeriodCycles;
					this.Channels[0].DoTick(this, pokeys, cycle, 0);
				}
				if (cycle == this.Channels[1].TickCycle) {
					if ((this.Audctl & 16) != 0)
						this.Channels[0].TickCycle = cycle + this.ReloadCycles1;
					else if ((this.Skctl & 8) != 0)
						this.Channels[0].TickCycle = cycle + this.Channels[0].PeriodCycles;
					this.Channels[1].DoTick(this, pokeys, cycle, 1);
				}
			}
		}

		internal void EndFrame(PokeyPair pokeys, int cycle)
		{
			GenerateUntilCycle(pokeys, cycle);
			this.PolyIndex += cycle;
			int m = (this.Audctl & 128) != 0 ? 237615 : 60948015;
			if (this.PolyIndex >= 2 * m)
				this.PolyIndex -= m;
			for (int i = 0; i < 4; i++) {
				int tickCycle = this.Channels[i].TickCycle;
				if (tickCycle != 8388608)
					this.Channels[i].TickCycle = tickCycle - cycle;
			}
		}

		internal bool IsSilent()
		{
			for (int i = 0; i < 4; i++)
				if ((this.Channels[i].Audc & 15) != 0)
					return false;
			return true;
		}

		internal void Mute(int mask)
		{
			for (int i = 0; i < 4; i++)
				this.Channels[i].SetMute((mask & 1 << i) != 0, 4, 0);
		}

		void InitMute(int cycle)
		{
			bool init = this.Init;
			int audctl = this.Audctl;
			this.Channels[0].SetMute(init && (audctl & 64) == 0, 2, cycle);
			this.Channels[1].SetMute(init && (audctl & 80) != 80, 2, cycle);
			this.Channels[2].SetMute(init && (audctl & 32) == 0, 2, cycle);
			this.Channels[3].SetMute(init && (audctl & 40) != 40, 2, cycle);
		}

		internal int Poke(PokeyPair pokeys, int addr, int data, int cycle)
		{
			int nextEventCycle = 8388608;
			switch (addr & 15) {
			case 0:
				if (data == this.Channels[0].Audf)
					break;
				GenerateUntilCycle(pokeys, cycle);
				this.Channels[0].Audf = data;
				switch (this.Audctl & 80) {
				case 0:
					this.Channels[0].PeriodCycles = this.DivCycles * (data + 1);
					break;
				case 16:
					this.Channels[1].PeriodCycles = this.DivCycles * (data + (this.Channels[1].Audf << 8) + 1);
					this.ReloadCycles1 = this.DivCycles * (data + 1);
					this.Channels[1].MuteUltrasound(cycle);
					break;
				case 64:
					this.Channels[0].PeriodCycles = data + 4;
					break;
				case 80:
					this.Channels[1].PeriodCycles = data + (this.Channels[1].Audf << 8) + 7;
					this.ReloadCycles1 = data + 4;
					this.Channels[1].MuteUltrasound(cycle);
					break;
				default:
					throw new NotImplementedException();
				}
				this.Channels[0].MuteUltrasound(cycle);
				break;
			case 1:
				this.Channels[0].SetAudc(this, pokeys, data, cycle);
				break;
			case 2:
				if (data == this.Channels[1].Audf)
					break;
				GenerateUntilCycle(pokeys, cycle);
				this.Channels[1].Audf = data;
				switch (this.Audctl & 80) {
				case 0:
				case 64:
					this.Channels[1].PeriodCycles = this.DivCycles * (data + 1);
					break;
				case 16:
					this.Channels[1].PeriodCycles = this.DivCycles * (this.Channels[0].Audf + (data << 8) + 1);
					break;
				case 80:
					this.Channels[1].PeriodCycles = this.Channels[0].Audf + (data << 8) + 7;
					break;
				default:
					throw new NotImplementedException();
				}
				this.Channels[1].MuteUltrasound(cycle);
				break;
			case 3:
				this.Channels[1].SetAudc(this, pokeys, data, cycle);
				break;
			case 4:
				if (data == this.Channels[2].Audf)
					break;
				GenerateUntilCycle(pokeys, cycle);
				this.Channels[2].Audf = data;
				switch (this.Audctl & 40) {
				case 0:
					this.Channels[2].PeriodCycles = this.DivCycles * (data + 1);
					break;
				case 8:
					this.Channels[3].PeriodCycles = this.DivCycles * (data + (this.Channels[3].Audf << 8) + 1);
					this.ReloadCycles3 = this.DivCycles * (data + 1);
					this.Channels[3].MuteUltrasound(cycle);
					break;
				case 32:
					this.Channels[2].PeriodCycles = data + 4;
					break;
				case 40:
					this.Channels[3].PeriodCycles = data + (this.Channels[3].Audf << 8) + 7;
					this.ReloadCycles3 = data + 4;
					this.Channels[3].MuteUltrasound(cycle);
					break;
				default:
					throw new NotImplementedException();
				}
				this.Channels[2].MuteUltrasound(cycle);
				break;
			case 5:
				this.Channels[2].SetAudc(this, pokeys, data, cycle);
				break;
			case 6:
				if (data == this.Channels[3].Audf)
					break;
				GenerateUntilCycle(pokeys, cycle);
				this.Channels[3].Audf = data;
				switch (this.Audctl & 40) {
				case 0:
				case 32:
					this.Channels[3].PeriodCycles = this.DivCycles * (data + 1);
					break;
				case 8:
					this.Channels[3].PeriodCycles = this.DivCycles * (this.Channels[2].Audf + (data << 8) + 1);
					break;
				case 40:
					this.Channels[3].PeriodCycles = this.Channels[2].Audf + (data << 8) + 7;
					break;
				default:
					throw new NotImplementedException();
				}
				this.Channels[3].MuteUltrasound(cycle);
				break;
			case 7:
				this.Channels[3].SetAudc(this, pokeys, data, cycle);
				break;
			case 8:
				if (data == this.Audctl)
					break;
				GenerateUntilCycle(pokeys, cycle);
				this.Audctl = data;
				this.DivCycles = (data & 1) != 0 ? 114 : 28;
				switch (data & 80) {
				case 0:
					this.Channels[0].PeriodCycles = this.DivCycles * (this.Channels[0].Audf + 1);
					this.Channels[1].PeriodCycles = this.DivCycles * (this.Channels[1].Audf + 1);
					break;
				case 16:
					this.Channels[0].PeriodCycles = this.DivCycles << 8;
					this.Channels[1].PeriodCycles = this.DivCycles * (this.Channels[0].Audf + (this.Channels[1].Audf << 8) + 1);
					this.ReloadCycles1 = this.DivCycles * (this.Channels[0].Audf + 1);
					break;
				case 64:
					this.Channels[0].PeriodCycles = this.Channels[0].Audf + 4;
					this.Channels[1].PeriodCycles = this.DivCycles * (this.Channels[1].Audf + 1);
					break;
				case 80:
					this.Channels[0].PeriodCycles = 256;
					this.Channels[1].PeriodCycles = this.Channels[0].Audf + (this.Channels[1].Audf << 8) + 7;
					this.ReloadCycles1 = this.Channels[0].Audf + 4;
					break;
				default:
					throw new NotImplementedException();
				}
				this.Channels[0].MuteUltrasound(cycle);
				this.Channels[1].MuteUltrasound(cycle);
				switch (data & 40) {
				case 0:
					this.Channels[2].PeriodCycles = this.DivCycles * (this.Channels[2].Audf + 1);
					this.Channels[3].PeriodCycles = this.DivCycles * (this.Channels[3].Audf + 1);
					break;
				case 8:
					this.Channels[2].PeriodCycles = this.DivCycles << 8;
					this.Channels[3].PeriodCycles = this.DivCycles * (this.Channels[2].Audf + (this.Channels[3].Audf << 8) + 1);
					this.ReloadCycles3 = this.DivCycles * (this.Channels[2].Audf + 1);
					break;
				case 32:
					this.Channels[2].PeriodCycles = this.Channels[2].Audf + 4;
					this.Channels[3].PeriodCycles = this.DivCycles * (this.Channels[3].Audf + 1);
					break;
				case 40:
					this.Channels[2].PeriodCycles = 256;
					this.Channels[3].PeriodCycles = this.Channels[2].Audf + (this.Channels[3].Audf << 8) + 7;
					this.ReloadCycles3 = this.Channels[2].Audf + 4;
					break;
				default:
					throw new NotImplementedException();
				}
				this.Channels[2].MuteUltrasound(cycle);
				this.Channels[3].MuteUltrasound(cycle);
				InitMute(cycle);
				break;
			case 9:
				for (int i = 0; i < 4; i++)
					this.Channels[i].DoStimer(cycle);
				break;
			case 14:
				this.Irqst |= data ^ 255;
				for (int i = 3;; i >>= 1) {
					if ((data & this.Irqst & (i + 1)) != 0) {
						if (this.Channels[i].TimerCycle == 8388608) {
							int t = this.Channels[i].TickCycle;
							while (t < cycle)
								t += this.Channels[i].PeriodCycles;
							this.Channels[i].TimerCycle = t;
							if (nextEventCycle > t)
								nextEventCycle = t;
						}
					}
					else
						this.Channels[i].TimerCycle = 8388608;
					if (i == 0)
						break;
				}
				break;
			case 15:
				if (data == this.Skctl)
					break;
				GenerateUntilCycle(pokeys, cycle);
				this.Skctl = data;
				bool init = (data & 3) == 0;
				if (this.Init && !init)
					this.PolyIndex = ((this.Audctl & 128) != 0 ? 237614 : 60948014) - cycle;
				this.Init = init;
				InitMute(cycle);
				this.Channels[2].SetMute((data & 16) != 0, 8, cycle);
				this.Channels[3].SetMute((data & 16) != 0, 8, cycle);
				break;
			default:
				break;
			}
			return nextEventCycle;
		}

		internal int CheckIrq(int cycle, int nextEventCycle)
		{
			for (int i = 3;; i >>= 1) {
				int timerCycle = this.Channels[i].TimerCycle;
				if (cycle >= timerCycle) {
					this.Irqst &= ~(i + 1);
					this.Channels[i].TimerCycle = 8388608;
				}
				else if (nextEventCycle > timerCycle)
					nextEventCycle = timerCycle;
				if (i == 0)
					break;
			}
			return nextEventCycle;
		}

		internal int StoreSample(byte[] buffer, int bufferOffset, int i, ASAPSampleFormat format)
		{
			this.IirAcc += this.DeltaBuffer[i] - (this.IirAcc * 3 >> 10);
			int sample = this.IirAcc >> 11;
			if (sample < -32767)
				sample = -32767;
			else if (sample > 32767)
				sample = 32767;
			switch (format) {
			case ASAPSampleFormat.U8:
				buffer[bufferOffset++] = (byte) ((sample >> 8) + 128);
				break;
			case ASAPSampleFormat.S16LE:
				buffer[bufferOffset++] = (byte) sample;
				buffer[bufferOffset++] = (byte) (sample >> 8);
				break;
			case ASAPSampleFormat.S16BE:
				buffer[bufferOffset++] = (byte) (sample >> 8);
				buffer[bufferOffset++] = (byte) sample;
				break;
			}
			return bufferOffset;
		}

		internal void AccumulateTrailing(int i)
		{
			this.IirAcc += this.DeltaBuffer[i] + this.DeltaBuffer[i + 1];
		}
	}

	class PokeyPair
	{
		internal PokeyPair()
		{
			int reg = 511;
			for (int i = 0; i < 511; i++) {
				reg = (((reg >> 5 ^ reg) & 1) << 8) + (reg >> 1);
				this.Poly9Lookup[i] = (byte) reg;
			}
			reg = 131071;
			for (int i = 0; i < 16385; i++) {
				reg = (((reg >> 5 ^ reg) & 255) << 9) + (reg >> 8);
				this.Poly17Lookup[i] = (byte) (reg >> 1);
			}
		}

		const int SampleFactorShift = 20;

		internal readonly byte[] Poly9Lookup = new byte[511];

		internal readonly byte[] Poly17Lookup = new byte[16385];

		int ExtraPokeyMask;

		internal readonly Pokey BasePokey = new Pokey();

		internal readonly Pokey ExtraPokey = new Pokey();

		internal int SampleFactor;

		internal int SampleOffset;

		internal int ReadySamplesStart;

		internal int ReadySamplesEnd;

		static int GetSampleFactor(int clock)
		{
			return (1445068800 + (clock >> 6)) / (clock >> 5);
		}

		internal void Initialize(bool ntsc, bool stereo)
		{
			this.ExtraPokeyMask = stereo ? 16 : 0;
			this.BasePokey.Initialize();
			this.ExtraPokey.Initialize();
			this.SampleFactor = ntsc ? 25837 : 26075;
			this.SampleOffset = 0;
			this.ReadySamplesStart = 0;
			this.ReadySamplesEnd = 0;
		}

		internal int Poke(int addr, int data, int cycle)
		{
			Pokey pokey = (addr & this.ExtraPokeyMask) != 0 ? this.ExtraPokey : this.BasePokey;
			return pokey.Poke(this, addr, data, cycle);
		}

		internal int Peek(int addr, int cycle)
		{
			Pokey pokey = (addr & this.ExtraPokeyMask) != 0 ? this.ExtraPokey : this.BasePokey;
			switch (addr & 15) {
			case 10:
				if (pokey.Init)
					return 255;
				int i = cycle + pokey.PolyIndex;
				if ((pokey.Audctl & 128) != 0)
					return this.Poly9Lookup[i % 511];
				i %= 131071;
				int j = i >> 3;
				i &= 7;
				return ((this.Poly17Lookup[j] >> i) + (this.Poly17Lookup[j + 1] << (8 - i))) & 255;
			case 14:
				return pokey.Irqst;
			default:
				return 255;
			}
		}

		internal void StartFrame()
		{
			this.BasePokey.StartFrame();
			if (this.ExtraPokeyMask != 0)
				this.ExtraPokey.StartFrame();
		}

		internal int EndFrame(int cycle)
		{
			this.BasePokey.EndFrame(this, cycle);
			if (this.ExtraPokeyMask != 0)
				this.ExtraPokey.EndFrame(this, cycle);
			this.SampleOffset += cycle * this.SampleFactor;
			this.ReadySamplesStart = 0;
			this.ReadySamplesEnd = this.SampleOffset >> 20;
			this.SampleOffset &= 1048575;
			return this.ReadySamplesEnd;
		}

		/**
		/// Fills buffer with samples from <c>DeltaBuffer</c>.
		 */
		internal int Generate(byte[] buffer, int bufferOffset, int blocks, ASAPSampleFormat format)
		{
			int i = this.ReadySamplesStart;
			int samplesEnd = this.ReadySamplesEnd;
			if (blocks < samplesEnd - i)
				samplesEnd = i + blocks;
			else
				blocks = samplesEnd - i;
			for (; i < samplesEnd; i++) {
				bufferOffset = this.BasePokey.StoreSample(buffer, bufferOffset, i, format);
				if (this.ExtraPokeyMask != 0)
					bufferOffset = this.ExtraPokey.StoreSample(buffer, bufferOffset, i, format);
			}
			if (i == this.ReadySamplesEnd) {
				this.BasePokey.AccumulateTrailing(i);
				this.ExtraPokey.AccumulateTrailing(i);
			}
			this.ReadySamplesStart = i;
			return blocks;
		}

		internal bool IsSilent()
		{
			return this.BasePokey.IsSilent() && this.ExtraPokey.IsSilent();
		}
	}

	internal static class CiResource
	{
		internal static readonly byte[] cmc_obx = {
			255, 255, 0, 5, 220, 12, 76, 15, 11, 76, 120, 5, 76, 203, 7, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 160, 227, 237, 227, 160, 240, 236, 225,
			249, 229, 242, 160, 246, 160, 178, 174, 177, 160, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255,
			255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 128, 128, 128, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 141, 110,
			5, 142, 111, 5, 140, 112, 5, 41, 112, 74, 74, 74, 170, 189, 145, 11,
			141, 169, 5, 189, 146, 11, 141, 170, 5, 169, 3, 141, 15, 210, 216, 165,
			254, 72, 165, 255, 72, 172, 112, 5, 174, 111, 5, 173, 110, 5, 32, 178,
			5, 104, 133, 255, 104, 133, 254, 96, 173, 118, 5, 133, 254, 173, 119, 5,
			133, 255, 160, 0, 138, 240, 28, 177, 254, 201, 143, 240, 4, 201, 239, 208,
			12, 202, 208, 9, 200, 192, 84, 176, 9, 152, 170, 16, 6, 200, 192, 84,
			144, 229, 96, 142, 104, 5, 32, 123, 6, 169, 0, 162, 9, 157, 69, 5,
			202, 16, 250, 141, 103, 5, 169, 1, 141, 113, 5, 169, 255, 141, 106, 5,
			173, 114, 5, 133, 254, 173, 115, 5, 133, 255, 160, 19, 177, 254, 170, 173,
			118, 5, 133, 254, 173, 119, 5, 133, 255, 172, 104, 5, 177, 254, 201, 207,
			208, 13, 152, 24, 105, 85, 168, 177, 254, 48, 15, 170, 76, 52, 6, 201,
			143, 240, 7, 201, 239, 240, 3, 136, 16, 226, 142, 108, 5, 142, 109, 5,
			96, 41, 15, 240, 245, 142, 218, 10, 142, 240, 10, 142, 255, 10, 140, 219,
			10, 140, 241, 10, 140, 0, 11, 96, 142, 114, 5, 134, 254, 140, 115, 5,
			132, 255, 24, 138, 105, 20, 141, 116, 5, 152, 105, 0, 141, 117, 5, 142,
			118, 5, 200, 200, 140, 119, 5, 160, 19, 177, 254, 141, 108, 5, 141, 109,
			5, 162, 8, 169, 0, 141, 113, 5, 157, 0, 210, 224, 3, 176, 8, 157,
			9, 5, 169, 255, 157, 57, 5, 202, 16, 233, 169, 128, 162, 3, 157, 75,
			5, 202, 16, 250, 96, 169, 1, 141, 113, 5, 169, 0, 240, 238, 41, 3,
			201, 3, 240, 240, 224, 64, 176, 236, 192, 26, 176, 232, 170, 169, 128, 157,
			75, 5, 169, 0, 157, 57, 5, 157, 60, 5, 157, 63, 5, 173, 111, 5,
			157, 12, 5, 173, 112, 5, 10, 10, 10, 133, 254, 24, 173, 114, 5, 105,
			48, 72, 173, 115, 5, 105, 1, 168, 104, 24, 101, 254, 157, 97, 5, 152,
			105, 0, 157, 100, 5, 24, 173, 114, 5, 105, 148, 133, 254, 173, 115, 5,
			105, 0, 133, 255, 173, 112, 5, 10, 109, 112, 5, 10, 168, 177, 254, 157,
			79, 5, 200, 177, 254, 157, 82, 5, 41, 7, 141, 110, 5, 200, 177, 254,
			157, 85, 5, 200, 177, 254, 157, 88, 5, 200, 177, 254, 157, 91, 5, 200,
			177, 254, 157, 94, 5, 160, 0, 173, 110, 5, 201, 3, 208, 2, 160, 2,
			201, 7, 208, 2, 160, 4, 185, 175, 11, 133, 254, 185, 176, 11, 133, 255,
			189, 85, 5, 74, 74, 74, 74, 24, 109, 111, 5, 141, 111, 5, 141, 194,
			7, 168, 173, 110, 5, 201, 7, 208, 15, 152, 10, 168, 177, 254, 157, 45,
			5, 200, 140, 111, 5, 76, 131, 7, 177, 254, 157, 45, 5, 189, 85, 5,
			41, 15, 24, 109, 111, 5, 141, 111, 5, 172, 111, 5, 173, 110, 5, 201,
			5, 8, 177, 254, 40, 240, 8, 221, 45, 5, 208, 3, 56, 233, 1, 157,
			48, 5, 189, 79, 5, 72, 41, 3, 168, 185, 181, 11, 157, 54, 5, 104,
			74, 74, 74, 74, 160, 62, 201, 15, 240, 16, 160, 55, 201, 14, 240, 10,
			160, 48, 201, 13, 240, 4, 24, 105, 0, 168, 185, 185, 11, 157, 51, 5,
			96, 216, 165, 252, 72, 165, 253, 72, 165, 254, 72, 165, 255, 72, 173, 113,
			5, 208, 3, 76, 2, 11, 173, 78, 5, 240, 3, 76, 107, 9, 173, 108,
			5, 205, 109, 5, 240, 3, 76, 88, 9, 173, 103, 5, 240, 3, 76, 220,
			8, 162, 2, 188, 75, 5, 48, 3, 157, 75, 5, 157, 69, 5, 202, 16,
			242, 173, 118, 5, 133, 252, 173, 119, 5, 133, 253, 172, 104, 5, 132, 254,
			204, 106, 5, 208, 25, 173, 107, 5, 240, 20, 173, 104, 5, 172, 105, 5,
			140, 104, 5, 206, 107, 5, 208, 232, 141, 104, 5, 168, 16, 226, 162, 0,
			177, 252, 201, 254, 208, 14, 172, 104, 5, 200, 196, 254, 240, 67, 140, 104,
			5, 76, 26, 8, 157, 66, 5, 24, 152, 105, 85, 168, 232, 224, 3, 144,
			223, 172, 104, 5, 177, 252, 16, 122, 201, 255, 240, 118, 74, 74, 74, 41,
			14, 170, 189, 161, 11, 141, 126, 8, 189, 162, 11, 141, 127, 8, 173, 67,
			5, 133, 255, 32, 147, 8, 140, 104, 5, 192, 85, 176, 4, 196, 254, 208,
			143, 164, 254, 140, 104, 5, 76, 2, 11, 32, 148, 6, 160, 255, 96, 48,
			251, 168, 96, 48, 247, 56, 152, 229, 255, 168, 96, 48, 239, 24, 152, 101,
			255, 168, 96, 48, 231, 141, 108, 5, 141, 109, 5, 200, 96, 48, 221, 173,
			68, 5, 48, 216, 141, 107, 5, 200, 140, 105, 5, 24, 152, 101, 255, 141,
			106, 5, 96, 136, 48, 10, 177, 252, 201, 143, 240, 4, 201, 239, 208, 243,
			200, 96, 162, 2, 189, 72, 5, 240, 5, 222, 72, 5, 16, 99, 189, 75,
			5, 208, 94, 188, 66, 5, 192, 64, 176, 87, 173, 116, 5, 133, 252, 173,
			117, 5, 133, 253, 177, 252, 133, 254, 24, 152, 105, 64, 168, 177, 252, 133,
			255, 37, 254, 201, 255, 240, 58, 188, 69, 5, 177, 254, 41, 192, 208, 12,
			177, 254, 41, 63, 157, 15, 5, 254, 69, 5, 16, 235, 201, 64, 208, 19,
			177, 254, 41, 63, 141, 111, 5, 189, 15, 5, 141, 112, 5, 32, 188, 6,
			76, 72, 9, 201, 128, 208, 10, 177, 254, 41, 63, 157, 72, 5, 254, 69,
			5, 202, 16, 144, 174, 103, 5, 232, 138, 41, 63, 141, 103, 5, 206, 109,
			5, 208, 14, 173, 108, 5, 141, 109, 5, 173, 103, 5, 208, 3, 238, 104,
			5, 172, 48, 5, 173, 82, 5, 41, 7, 201, 5, 240, 4, 201, 6, 208,
			1, 136, 140, 39, 5, 160, 0, 201, 5, 240, 4, 201, 6, 208, 2, 160,
			2, 201, 7, 208, 2, 160, 40, 140, 44, 5, 162, 2, 189, 82, 5, 41,
			224, 157, 40, 5, 189, 97, 5, 133, 252, 189, 100, 5, 133, 253, 189, 57,
			5, 201, 255, 240, 54, 201, 15, 208, 32, 189, 63, 5, 240, 45, 222, 63,
			5, 189, 63, 5, 208, 37, 188, 9, 5, 240, 1, 136, 152, 157, 9, 5,
			189, 88, 5, 157, 63, 5, 76, 229, 9, 189, 57, 5, 74, 168, 177, 252,
			144, 4, 74, 74, 74, 74, 41, 15, 157, 9, 5, 188, 45, 5, 189, 82,
			5, 41, 7, 201, 1, 208, 31, 136, 152, 200, 221, 48, 5, 8, 169, 1,
			40, 208, 2, 10, 10, 61, 60, 5, 240, 12, 188, 48, 5, 192, 255, 208,
			5, 169, 0, 157, 9, 5, 152, 157, 36, 5, 169, 1, 141, 110, 5, 189,
			57, 5, 201, 15, 240, 56, 41, 7, 168, 185, 205, 12, 133, 254, 189, 57,
			5, 41, 8, 8, 138, 40, 24, 240, 2, 105, 3, 168, 185, 91, 5, 37,
			254, 240, 27, 189, 51, 5, 157, 36, 5, 142, 110, 5, 202, 16, 8, 141,
			39, 5, 169, 0, 141, 44, 5, 232, 189, 54, 5, 157, 40, 5, 189, 57,
			5, 41, 15, 201, 15, 240, 16, 254, 57, 5, 189, 57, 5, 201, 15, 208,
			6, 189, 88, 5, 157, 63, 5, 189, 75, 5, 16, 10, 189, 9, 5, 208,
			5, 169, 64, 157, 75, 5, 254, 60, 5, 160, 0, 189, 82, 5, 74, 74,
			74, 74, 144, 1, 136, 74, 144, 1, 200, 24, 152, 125, 45, 5, 157, 45,
			5, 189, 48, 5, 201, 255, 208, 2, 160, 0, 24, 152, 125, 48, 5, 157,
			48, 5, 202, 48, 3, 76, 150, 9, 173, 40, 5, 141, 43, 5, 173, 82,
			5, 41, 7, 170, 160, 3, 173, 110, 5, 240, 3, 188, 213, 12, 152, 72,
			185, 185, 12, 8, 41, 127, 170, 152, 41, 3, 10, 168, 189, 36, 5, 153,
			0, 210, 200, 189, 9, 5, 224, 3, 208, 3, 173, 9, 5, 29, 40, 5,
			40, 16, 2, 169, 0, 153, 0, 210, 104, 168, 136, 41, 3, 208, 207, 160,
			8, 173, 44, 5, 153, 0, 210, 24, 104, 133, 255, 104, 133, 254, 104, 133,
			253, 104, 133, 252, 96, 104, 170, 240, 78, 201, 2, 240, 6, 104, 104, 202,
			208, 251, 96, 165, 20, 197, 20, 240, 252, 173, 36, 2, 201, 134, 208, 7,
			173, 37, 2, 201, 11, 240, 230, 173, 36, 2, 141, 143, 11, 173, 37, 2,
			141, 144, 11, 169, 134, 141, 36, 2, 169, 11, 141, 37, 2, 104, 104, 240,
			3, 56, 233, 1, 141, 93, 11, 104, 168, 104, 170, 169, 112, 32, 120, 5,
			169, 0, 162, 0, 76, 120, 5, 165, 20, 197, 20, 240, 252, 173, 36, 2,
			201, 134, 208, 174, 173, 37, 2, 201, 11, 208, 167, 173, 143, 11, 141, 36,
			2, 173, 144, 11, 141, 37, 2, 169, 64, 76, 120, 5, 32, 203, 7, 144,
			3, 32, 117, 11, 76, 255, 255, 178, 5, 221, 5, 168, 6, 59, 6, 123,
			6, 148, 6, 159, 6, 82, 6, 147, 8, 153, 8, 157, 8, 165, 8, 173,
			8, 183, 8, 205, 8, 185, 11, 250, 11, 59, 12, 128, 160, 32, 64, 255,
			241, 228, 215, 203, 192, 181, 170, 161, 152, 143, 135, 127, 120, 114, 107, 101,
			95, 90, 85, 80, 75, 71, 67, 63, 60, 56, 53, 50, 47, 44, 42, 39,
			37, 35, 33, 31, 29, 28, 26, 24, 23, 22, 20, 19, 18, 17, 16, 15,
			14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0,
			0, 0, 0, 0, 242, 233, 218, 206, 191, 182, 170, 161, 152, 143, 137, 128,
			122, 113, 107, 101, 95, 0, 86, 80, 103, 96, 90, 85, 81, 76, 72, 67,
			63, 61, 57, 52, 51, 57, 45, 42, 40, 37, 36, 33, 31, 30, 0, 0,
			15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
			0, 56, 11, 140, 10, 0, 10, 106, 9, 232, 8, 106, 8, 239, 7, 128,
			7, 8, 7, 174, 6, 70, 6, 230, 5, 149, 5, 65, 5, 246, 4, 176,
			4, 110, 4, 48, 4, 246, 3, 187, 3, 132, 3, 82, 3, 34, 3, 244,
			2, 200, 2, 160, 2, 122, 2, 85, 2, 52, 2, 20, 2, 245, 1, 216,
			1, 189, 1, 164, 1, 141, 1, 119, 1, 96, 1, 78, 1, 56, 1, 39,
			1, 21, 1, 6, 1, 247, 0, 232, 0, 219, 0, 207, 0, 195, 0, 184,
			0, 172, 0, 162, 0, 154, 0, 144, 0, 136, 0, 127, 0, 120, 0, 112,
			0, 106, 0, 100, 0, 94, 0, 87, 0, 82, 0, 50, 0, 10, 0, 0,
			1, 2, 131, 0, 1, 2, 3, 1, 0, 2, 131, 1, 0, 2, 3, 1,
			2, 128, 3, 128, 64, 32, 16, 8, 4, 2, 1, 3, 3, 3, 3, 7,
			11, 15, 19 };
	}
}
