-- Day 7 (set operations): tag schema for the SQL exercise.
--
-- These tables exist in quotes.db only. They are deliberately NOT part of the
-- EF Core model: the API has no tagging feature, and adding a migration whose
-- only purpose is to serve a reporting query would put dead tables in the
-- application model. EF ignores tables outside its model, so the app is
-- unaffected (verified by starting it with an unknown table present).

DROP TABLE IF EXISTS QuoteTags;
DROP TABLE IF EXISTS Tags;

CREATE TABLE Tags (
    Id       INTEGER PRIMARY KEY,
    Name     TEXT NOT NULL,
    Category TEXT NOT NULL CHECK (Category IN ('classic', 'modern')),
    UNIQUE (Name, Category)
);

CREATE TABLE QuoteTags (
    QuoteId INTEGER NOT NULL REFERENCES Quotes(Id),
    TagId   INTEGER NOT NULL REFERENCES Tags(Id),
    PRIMARY KEY (QuoteId, TagId)
);

INSERT INTO Tags (Name, Category) VALUES
    ('correctness',  'classic'),
    ('simplicity',   'classic'),
    ('pioneering',   'classic'),
    ('concurrency',  'modern'),
    ('observability','modern'),
    ('simplicity',   'modern');

-- Tag quotes by author. Dijkstra and Knuth get tags in both categories so the
-- INTERSECT question has a non-empty answer; Ada Lovelace and Margaret Hamilton
-- are classic-only; Alan Turing and Grace Hopper are left untagged so the
-- EXCEPT question has one too.

INSERT INTO QuoteTags (QuoteId, TagId)
SELECT q.Id, t.Id FROM Quotes q, Tags t
WHERE (q.Author = 'Edsger Dijkstra'   AND t.Name = 'simplicity'    AND t.Category = 'classic')
   OR (q.Author = 'Edsger Dijkstra'   AND t.Name = 'correctness'   AND t.Category = 'classic')
   OR (q.Author = 'Edsger Dijkstra'   AND t.Name = 'concurrency'   AND t.Category = 'modern')
   OR (q.Author = 'Donald Knuth'      AND t.Name = 'correctness'   AND t.Category = 'classic')
   OR (q.Author = 'Donald Knuth'      AND t.Name = 'simplicity'    AND t.Category = 'modern')
   OR (q.Author = 'Tony Hoare'        AND t.Name = 'correctness'   AND t.Category = 'classic')
   OR (q.Author = 'Tony Hoare'        AND t.Name = 'concurrency'   AND t.Category = 'modern')
   OR (q.Author = 'Leslie Lamport'    AND t.Name = 'concurrency'   AND t.Category = 'modern')
   OR (q.Author = 'Leslie Lamport'    AND t.Name = 'observability' AND t.Category = 'modern')
   OR (q.Author = 'Ada Lovelace'      AND t.Name = 'pioneering'    AND t.Category = 'classic')
   OR (q.Author = 'Margaret Hamilton' AND t.Name = 'pioneering'    AND t.Category = 'classic')
   OR (q.Author = 'Barbara Liskov'    AND t.Name = 'simplicity'    AND t.Category = 'modern')
   OR (q.Author = 'Shruti'            AND t.Name = 'observability' AND t.Category = 'modern');
