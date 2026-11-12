# PCG32 来源与许可

`src/Sanguo.BattleCore/Randomness/Pcg32.cs` 将 PCG minimal C 的 XSH RR 输出、播种和无偏有界抽样移植为 C#，补充随机状态、算法版本和消费次数。初始化两次推进不计入业务消费次数。

- 原作者：Melissa O'Neill，Copyright 2014。
- 原实现：https://github.com/imneme/pcg-c-basic/blob/master/pcg_basic.c
- 官方说明：https://www.pcg-random.org/download.html
- 许可：Apache License 2.0，全文见本目录 `Apache-2.0.txt`。
- 固定向量：seed=42、stream=54，前六次输出为 `a15c02b7 7b47f409 ba1d3330 83d2f293 bfa4784b cbed606e`。

当前固定流：战斗抽样54；世界、AI与表现流尚未实现，后续使用不同stream ID，不共用业务随机对象。
