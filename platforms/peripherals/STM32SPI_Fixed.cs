// Derived from 1.15.0 STM32SPI.cs
//
// Original assignment:
//
// Copyright (c) 2010-2023 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Utilities.Collections;

namespace Antmicro.Renode.Peripherals.SPI
{
    public sealed class STM32SPI_Fixed : NullRegistrationPointPeripheralContainer<ISPIPeripheral>, IWordPeripheral, IDoubleWordPeripheral, IBytePeripheral, IKnownSize
    {
        public STM32SPI_Fixed(Machine machine, int bufferCapacity = DefaultBufferCapacity) : base(machine)
        {
            receiveBuffer = new CircularBuffer<byte>(bufferCapacity);
            IRQ = new GPIO();
            DMAReceive = new GPIO();
            DMATransmit = new GPIO();
            registers = new DoubleWordRegisterCollection(this);
            SetupRegisters();
            Reset();
        }

        // We can't use AllowedTranslations because then WriteByte/WriteWord will trigger
        // an additional read (see ReadWriteExtensions:WriteByteUsingDword).
        // We can't have this happen for the data register.
        public byte ReadByte(long offset)
        {
            // byte interface is there for DMA
            if(offset % 4 == 0)
            {
                return (byte)ReadDoubleWord(offset);
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset % 4 == 0)
            {
                WriteDoubleWord(offset, (uint)value);
            }
            else
            {
                this.LogUnhandledWrite(offset, value);
            }
        }

        public ushort ReadWord(long offset)
        {
            return (ushort)ReadDoubleWord(offset);
        }

        public void WriteWord(long offset, ushort value)
        {
            WriteDoubleWord(offset, (uint)value);
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public override void Reset()
        {
            IRQ.Unset();
            DMAReceive.Unset();
            DMATransmit.Unset();
            lock(receiveBuffer)
            {
                receiveBuffer.Clear();
            }
            registers.Reset();
        }

        public long Size
        {
            get
            {
                return 0x400;
            }
        }

        public GPIO IRQ { get; }

        public GPIO DMAReceive { get; }

        public GPIO DMATransmit { get; }

        private uint HandleDataRead()
        {
            IRQ.Unset();
            lock(receiveBuffer)
            {
                if(receiveBuffer.TryDequeue(out var value))
                {
                    Update();
                    return value;
                }
                // We don't warn when the data register is read while it's empty because the HAL
                // (for example L0, F4) does this intentionally.
                // See https://github.com/STMicroelectronics/STM32CubeL0/blob/bec4e499a74de98ab60784bf2ef1912bee9c1a22/Drivers/STM32L0xx_HAL_Driver/Src/stm32l0xx_hal_spi.c#L1368-L1372
                return 0;
            }
        }

        private void HandleDataWrite(uint value)
        {
            this.Log(LogLevel.Debug, "HandleDataWrite: value 0x{0:X}", value);
            IRQ.Unset();
            lock(receiveBuffer)
            {
                var peripheral = RegisteredPeripheral;
                if(peripheral == null)
                {
                    this.Log(LogLevel.Warning, "SPI transmission while no SPI peripheral is connected.");
                    receiveBuffer.Enqueue(0x0);
                    return;
                }
                var response = peripheral.Transmit((byte)value); // currently byte mode is the only one we support
                //
                DMATransmit.Unset();
                this.Log(LogLevel.Debug, "HandleDataWrite: set DMATransmit.Unset()");
                //
                receiveBuffer.Enqueue(response);
                if(rxDmaEnable.Value)
                {
                    // This blink is used to signal the DMA that it should perform the peripheral -> memory transaction now
                    // Without this signal DMA will never move data from the receive buffer to memory
                    // See STM32DMA:OnGPIO
                    this.Log(LogLevel.Debug, "HandleDataWrite: rxDmaEnable {0} triggering DMAReceive.Blink()", rxDmaEnable.Value);
                    DMAReceive.Blink();
                }
                this.NoisyLog("Transmitted 0x{0:X}, received 0x{1:X}.", value, response);
            }
            Update();
        }

        private void Update()
        {
            var rxBufferNotEmpty = receiveBuffer.Count != 0;
            var rxBufferNotEmptyInterruptFlag = rxBufferNotEmpty && rxBufferNotEmptyInterruptEnable.Value;

            this.Log(LogLevel.Debug, "Update: receiveBuffer.Count {0} : rxBufferNotEmpty {1} rxBufferNotEmptyInterruptFlag {2} txBufferEmptyInterruptEnable {3}", receiveBuffer.Count, rxBufferNotEmpty, rxBufferNotEmptyInterruptFlag, txBufferEmptyInterruptEnable.Value);

            // At the moment this model assumes that transmit is
            // always empty (SPI_SR:TXE is always true) and that
            // transmissions are instantaneous

            // CONSIDER: Only doing the following if txDmaEnable changing false->true (if already true then do nothing)
            // For our SPI driver the DMA channels may not be initialised and so the target stream is still false

            this.Log(LogLevel.Debug, "Update: txDmaEnable {0} rxDmaEnable {1}", txDmaEnable.Value, rxDmaEnable.Value);
            if(txDmaEnable.Value) // && (..1==TXE..))
            {
                // TODO:IMPLEMENT: We may be able to use IsConnected
                // and Endpoints and Connect methods to dynamically
                // change the DMA channel stream mapping at run-time
                // to avoid hardwiring in stm32f2.repl
                //
                //this.Log(LogLevel.Debug, "Update: DMATransmit.IsConnected {0}", DMATransmit.IsConnected);
                //this.Log(LogLevel.Debug, "Update: DMATransmit.Endpoints {0}", DMATransmit.Endpoints);
                this.Log(LogLevel.Debug, "Update: DMATransmit {0} IsSet {1}", DMATransmit, DMATransmit.IsSet);
                if(!DMATransmit.IsSet)
                {
                    this.Log(LogLevel.Debug, "Update: txDmaEnable and TXE so triggering DMATransmit.Set()");
                    DMATransmit.Unset(); // clear any previous stale state prior to:
                    DMATransmit.Set(); // indicate DMA can perform M2P
                }
            }
            else
            {
                DMATransmit.Unset();
            }

            IRQ.Set(txBufferEmptyInterruptEnable.Value || rxBufferNotEmptyInterruptFlag);
        }

        private void SetupRegisters()
        {
            // Some fields relate to the physical layer of the SPI protocol, these are not
            // taken into account in Renode and are defined as flags or value fields without
            // a corresponding RegisterField or read/write callbacks. They are marked with
            // comments.
            Registers.Control1.Define(registers)
                .WithFlag(0, name: "CPHA") // Physical
                .WithFlag(1, name: "CPOL") // Physical
                .WithFlag(2, writeCallback: (_, value) =>
                {
                    if(!value)
                    {
                        this.Log(LogLevel.Warning, "Slave mode is not supported");
                    }
                }, name: "MSTR")
                .WithValueField(3, 3, name: "Baud") // Physical
                .WithFlag(6, changeCallback: (oldValue, newValue) =>
                {
                    if(!newValue)
                    {
                        IRQ.Unset();
                    }
                }, name: "SpiEnable")
                .WithFlag(7, name: "LSBFIRST") // Physical
                // NOTE: eCos driver sets SSI|SSM in stm32_transaction_begin()
                .WithFlag(8, name: "SSI")
                .WithFlag(9, name: "SSM")
                .WithTaggedFlag("RXONLY", 10)
                .WithTaggedFlag("DFF", 11)
                .WithTaggedFlag("CRCNEXT", 12)
                .WithTaggedFlag("CRCEN", 13)
                .WithTaggedFlag("BIDIOE", 14)
                .WithTaggedFlag("BIDIMODE", 15);

            // We track TXDMAEN and other R/W flags so that S/W can
            // read configured state even if the model ignores the
            // feature
            Registers.Control2.Define(registers)
                .WithFlag(0, out rxDmaEnable, name: "RXDMAEN")
                .WithFlag(1, out txDmaEnable, name: "TXDMAEN")
                .WithFlag(2, out ssOutputEnable, name: "SSOE")
                .WithReservedBits(3, 1)
                .WithFlag(4, out frameFormat, name: "FRF")
                .WithFlag(5, out errInterruptEnable, name: "ERRIE")
                .WithFlag(6, out rxBufferNotEmptyInterruptEnable, name: "RXNEIE")
                .WithFlag(7, out txBufferEmptyInterruptEnable, name: "TXEIE")
                // The following fields are F7 fields that the eCos
                // driver will set knowing that they are benign on
                // the STM32F2 (really should be reserved). This is
                // being done instead of the original
                // WithReservedBits to avoid repeated WARNINGs being
                // raised.
                //.WithReservedBits(8, 24)
                .WithValueField(8, 5, name: "Reserved")
                .WithReservedBits(13, 19)
                .WithWriteCallback((_, __) => Update());

            Registers.Status.Define(registers, 2)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => receiveBuffer.Count != 0, name: "RXNE")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => true, name: "TXE") // transfers are instant
                .WithTaggedFlag("CHSIDE", 2) // r/o
                .WithTaggedFlag("UDR", 3) // r/o
                .WithTaggedFlag("CRCERR", 4) // rc_w0
                .WithTaggedFlag("MODF", 5) // r/o
                .WithTaggedFlag("OVR", 6) // r/o
                .WithTaggedFlag("BSY", 7) // r/o
                .WithTaggedFlag("FRE", 8) // r/o
                .WithReservedBits(9, 23);

            Registers.Data.Define(registers)
                .WithValueField(0, 16, valueProviderCallback: _ => HandleDataRead(),
                    writeCallback: (_, value) => HandleDataWrite((uint)value), name: "DR")
                .WithReservedBits(16, 16);

            Registers.CRCPolynomial.Define(registers, 7)
                .WithTag("CRCPOLY", 0, 16)
                .WithReservedBits(16, 16);

            Registers.ReceivedCRC.Define(registers)
                .WithTag("RxCRC", 0, 16) // r/o
                .WithReservedBits(16, 16);

            Registers.TransmittedCRC.Define(registers)
                .WithTag("TxCRC", 0, 16) // r/o
                .WithReservedBits(16, 16);

            Registers.I2SConfiguration.Define(registers)
                .WithTaggedFlag("CHLEN", 0)
                .WithTag("DATLEN", 1, 2)
                .WithTaggedFlag("CKPOL", 3)
                .WithTag("I2SSTD", 4, 2)
                .WithReservedBits(6, 1)
                .WithTaggedFlag("PCMSYNC", 7)
                .WithTag("I2SCFG", 8, 2)
                .WithFlag(10, FieldMode.Read | FieldMode.WriteOneToClear, writeCallback: (oldValue, newValue) =>
                {
                    // write one to clear to keep this bit 0
                    if(newValue)
                    {
                        this.Log(LogLevel.Warning, "Trying to enable not supported I2S mode.");
                    }
                }, name: "I2SE")
                .WithTaggedFlag("I2SMOD", 11)
                .WithReservedBits(12, 20);

            Registers.I2SPrescaler.Define(registers, 2)
                .WithTag("I2SDIV", 0, 8)
                .WithTaggedFlag("ODD", 8)
                .WithTaggedFlag("MCKOE", 9)
                .WithReservedBits(10, 22);
        }

        private DoubleWordRegisterCollection registers;
        private IFlagRegisterField txBufferEmptyInterruptEnable, rxBufferNotEmptyInterruptEnable, rxDmaEnable;
        private IFlagRegisterField txDmaEnable, ssOutputEnable, frameFormat, errInterruptEnable;

        private readonly CircularBuffer<byte> receiveBuffer;

        private const int DefaultBufferCapacity = 4;

        private enum Registers
        {
            Control1 = 0x0, // SPI_CR1,
            Control2 = 0x4, // SPI_CR2
            Status = 0x8, // SPI_SR
            Data = 0xC, // SPI_DR
            CRCPolynomial = 0x10, // SPI_CRCPR
            ReceivedCRC = 0x14, // SPI_RXCRCR
            TransmittedCRC = 0x18, // SPI_TXCRCR
            I2SConfiguration = 0x1C, // SPI_I2SCFGR
            I2SPrescaler = 0x20, // SPI_I2SPR
        }
    }
}
