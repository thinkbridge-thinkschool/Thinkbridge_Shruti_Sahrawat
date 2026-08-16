-- Day 7 (set operations): three business questions answered with UNION /
-- INTERSECT / EXCEPT. Requires the tag tables from tags-schema.sql.

-- Q1: authors with quotes but no tags. Set difference -> EXCEPT.
SELECT DISTINCT Author FROM Quotes WHERE IsDeleted = 0
EXCEPT
SELECT DISTINCT q.Author
FROM Quotes q
JOIN QuoteTags qt ON qt.QuoteId = q.Id
WHERE q.IsDeleted = 0;

-- Q2: authors in both the classic and modern sets. Membership of both -> INTERSECT.
SELECT DISTINCT q.Author
FROM Quotes q
JOIN QuoteTags qt ON qt.QuoteId = q.Id
JOIN Tags t ON t.Id = qt.TagId
WHERE q.IsDeleted = 0 AND t.Category = 'classic'
INTERSECT
SELECT DISTINCT q.Author
FROM Quotes q
JOIN QuoteTags qt ON qt.QuoteId = q.Id
JOIN Tags t ON t.Id = qt.TagId
WHERE q.IsDeleted = 0 AND t.Category = 'modern';

-- Q3: combined distinct tag list across both categories. Distinct -> UNION.
SELECT Name FROM Tags WHERE Category = 'classic'
UNION
SELECT Name FROM Tags WHERE Category = 'modern';
