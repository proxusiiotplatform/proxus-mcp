import asyncio
from pymodbus.datastore import ModbusSequentialDataBlock, ModbusDeviceContext, ModbusServerContext
from pymodbus.server import StartAsyncTcpServer
# hr:0-1 = 230.5 as float32 stored word-swapped (CDAB), hr:2 = 1234, hr:3 = -5 (int16)
hr = [0x8000, 0x4366, 1234, 0xFFFB] + [0] * 20
ctx = ModbusServerContext(devices=ModbusDeviceContext(
    hr=ModbusSequentialDataBlock(1, hr),
    ir=ModbusSequentialDataBlock(1, [42] * 20),
    co=ModbusSequentialDataBlock(1, [1, 0, 1, 1] + [0] * 20),
    di=ModbusSequentialDataBlock(1, [0] * 20)), single=True)
asyncio.run(StartAsyncTcpServer(context=ctx, address=("127.0.0.1", 15020)))
