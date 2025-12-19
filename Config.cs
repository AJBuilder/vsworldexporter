using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WorldExporter
{
    [ProtoContract]
    public class Config
    {
        [ProtoMember(1)]
        public string outputDirectory;
    }
}
